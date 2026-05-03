using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmText.OpenAIAzure.Models;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.LlmText.OpenAIAzure;

public sealed class AzureOpenAiTextProvider(IAgentLogic agentLogic, AgentConfig agentConfig, ILogger<AzureOpenAiTextProvider> logger) : ILlmTextProvider, IDisposable
{
    private AzureOpenAiTextConfig? _config;
    private HttpClient? _httpClient;

    public const string ProviderKey = "azure-openai-text";

    public string Key => ProviderKey;

    // Hardcoded deployment-name list — these are the Azure deployments this user maintains.
    // Update here when a new deployment is added (and redeploy the agent).
    private static readonly string[] DefaultModels = ["gpt-5.2-chat"];

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
        new() { Key = "endpoint", Label = "Endpoint", Type = "String", Required = true },
        new() { Key = "apiVersion", Label = "API Version", Type = "String", DefaultValue = "2025-04-01-preview" }
    ];

    public IReadOnlyList<string> Models => DefaultModels;

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<AzureOpenAiTextConfig>(configuration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(_config.Endpoint))
            throw new InvalidOperationException("endpoint is required.");

        var baseUri = _config.Endpoint.TrimEnd('/');
        _httpClient?.Dispose();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUri + "/")
        };
        _httpClient.DefaultRequestHeaders.Add("api-key", _config.ApiKey);

        logger.LogInformation("Text provider configured with {ModelCount} models at {Endpoint}",
            DefaultModels.Length, _config.Endpoint);
    }

    public void Dispose() => _httpClient?.Dispose();

    /// <inheritdoc />
    public int? GetContextWindow(string model)
    {
        // Azure deployment names are user-chosen, so this is a best-effort table based on the
        // underlying model family encoded in the deployment name. Unknown models return null
        // and callers fall back to CompactionConfig.MaxContextTokens.
        if (model.Contains("gpt-5", StringComparison.OrdinalIgnoreCase)) return 400_000;
        if (model.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase)) return 128_000;
        if (model.Contains("gpt-4", StringComparison.OrdinalIgnoreCase)) return 128_000;
        return null;
    }

    /// <summary>
    /// Detects Azure OpenAI's context-length error. Azure returns HTTP 400 with an error
    /// object whose <c>code</c> is <c>context_length_exceeded</c>, or a message mentioning
    /// "maximum context length". Heuristic — tune as new error shapes appear.
    /// </summary>
    private static bool IsContextOverflow(System.Net.HttpStatusCode status, string body)
    {
        if (status != System.Net.HttpStatusCode.BadRequest) return false;
        return body.Contains("context_length_exceeded", StringComparison.Ordinal)
            || body.Contains("maximum context length", StringComparison.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, Message userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var conversationId = conversation.Id;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogDebug("StreamAsync called for conversation {ConversationId}", conversationId);

        // Populate the per-conversation context window cache on first turn or after a model
        // switch. Used by the compaction threshold — lets it scale with the active model
        // rather than relying on a global constant.
        if (conversation.ContextWindowTokens is null)
        {
            var window = GetContextWindow(conversation.TextModel);
            if (window is not null)
                conversation.ContextWindowTokens = window;
        }

        // Persist the caller-supplied user message
        agentLogic.AddMessage(conversationId, userMessage);

        // Build request once — messages are mutated across tool call rounds
        var request = new ChatCompletionRequest
        {
            Messages = BuildChatMessages(conversation),
            Tools = BuildTools(),
            ToolChoice = agentLogic.Tools.Count > 0 ? "auto" : null,
            Stream = true,
            StreamOptions = new StreamOptions { IncludeUsage = true }
        };

        var url = $"openai/deployments/{conversation.TextModel}/chat/completions?api-version={_config.ApiVersion}";

        // Completion loop (handles tool calls across streaming rounds — cap configurable via AgentConfig.MaxToolRounds)
        var maxToolRounds = agentConfig.MaxToolRounds;
        var overflowRetried = false;
        for (var round = 0; round < maxToolRounds; round++)
        {
            // Inner loop lets us retry ONCE on a context-length error by running compaction
            // and rebuilding the request. overflowRetried is scoped to this CompleteAsync
            // call — at most one recovery attempt per turn.
            HttpResponseMessage httpResponse;
            while (true)
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(request)
                };
                httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

                if (httpResponse.IsSuccessStatusCode) break;

                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                if (!overflowRetried && IsContextOverflow(httpResponse.StatusCode, errorBody))
                {
                    overflowRetried = true;
                    logger.LogWarning(
                        "Azure OpenAI context overflow for conversation {ConversationId} — compacting and retrying once",
                        conversationId);

                    httpResponse.Dispose();

                    var compacted = await agentLogic.CompactAsync(conversationId, CompactionReason.Overflow, null, ct);
                    if (!compacted)
                    {
                        throw new HttpRequestException(
                            "Context overflow, and compaction could not reduce history (already minimal or disabled).");
                    }

                    // Rebuild from the compacted state — GetConversation re-reads with the new
                    // Conversation.Context set by compaction.
                    var compactedConv = agentLogic.GetConversation(conversationId) ?? conversation;
                    request.Messages = BuildChatMessages(compactedConv);
                    continue;
                }

                logger.LogError("Azure OpenAI returned {StatusCode} for conversation {ConversationId}: {ErrorBody}",
                    (int)httpResponse.StatusCode, conversationId, errorBody);
                throw new HttpRequestException(
                    $"Azure OpenAI returned {(int)httpResponse.StatusCode}: {errorBody}");
            }

            // Read SSE stream, accumulating text content and tool call fragments
            var fullContent = new System.Text.StringBuilder();
            var toolCallAccumulator = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
            string? finishReason = null;
            int? promptTokens = null;
            int? completionTokens = null;

            using var stream = await httpResponse.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ")) continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]") break;

                var chunk = JsonSerializer.Deserialize<ChatCompletionResponse>(data);

                // Capture usage from the final chunk (sent when stream_options.include_usage is true)
                if (chunk?.Usage is not null)
                {
                    promptTokens = chunk.Usage.PromptTokens;
                    completionTokens = chunk.Usage.CompletionTokens;
                }

                var choice = chunk?.Choices?.FirstOrDefault();
                if (choice is null) continue;

                finishReason = choice.FinishReason ?? finishReason;
                var delta = choice.Delta;
                if (delta is null) continue;

                // Accumulate text content and yield to caller
                if (delta.Content is { Length: > 0 } content)
                {
                    fullContent.Append(content);
                    yield return new TextDelta(content);
                }

                // Accumulate streamed tool call fragments (name and arguments arrive incrementally)
                if (delta.ToolCalls is { Count: > 0 })
                {
                    foreach (var tc in delta.ToolCalls)
                    {
                        var idx = tc.Index ?? 0;
                        if (!toolCallAccumulator.ContainsKey(idx))
                            toolCallAccumulator[idx] = (tc.Id ?? "", tc.Function?.Name ?? "", new System.Text.StringBuilder());

                        var entry = toolCallAccumulator[idx];
                        if (tc.Id is not null) entry.Id = tc.Id;
                        if (tc.Function?.Name is not null) entry.Name = tc.Function.Name;
                        if (tc.Function?.Arguments is not null) entry.Args.Append(tc.Function.Arguments);
                        toolCallAccumulator[idx] = entry;
                    }
                }
            }

            // If the model requested tool calls, execute them and loop
            if (finishReason == "tool_calls" && toolCallAccumulator.Count > 0)
            {
                logger.LogDebug("Tool calls requested in conversation {ConversationId}: {ToolNames}",
                    conversationId, string.Join(", ", toolCallAccumulator.Values.Select(t => t.Name)));

                // Build the tool calls list for the wire message and persistence
                var assembledToolCalls = toolCallAccumulator.OrderBy(kv => kv.Key).Select(kv => new ToolCall
                {
                    Id = kv.Value.Id,
                    Type = "function",
                    Function = new ToolCallFunction
                    {
                        Name = kv.Value.Name,
                        Arguments = kv.Value.Args.ToString()
                    }
                }).ToList();

                // Persist assistant message with tool calls
                agentLogic.AddMessage(conversationId, new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    ConversationId = conversationId,
                    Role = "assistant",
                    ToolCalls = JsonSerializer.Serialize(assembledToolCalls),
                    Modality = MessageModality.Text
                });
                request.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = assembledToolCalls
                });

                // Execute each tool call, yield events, persist results
                foreach (var (_, (id, name, args)) in toolCallAccumulator.OrderBy(kv => kv.Key))
                {
                    var argsString = args.ToString();
                    yield return new ToolCallEvent(id, name, argsString);

                    logger.LogDebug("Executing tool {ToolName} for conversation {ConversationId}", name, conversationId);
                    var result = await agentLogic.ExecuteToolAsync(conversationId, name, argsString, ct);

                    yield return new ToolResultEvent(id, name, result);

                    // Persist tool result: Content keeps the compact summary (for UI and
                    // backward compat), FullToolResult carries the raw output to disk via the
                    // store's blob writer. Next turn's BuildChatMessages loads the blob back.
                    agentLogic.AddMessage(conversationId, new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = conversationId,
                        Role = "tool",
                        Content = ToolResultSummary.Create(name, result),
                        FullToolResult = result,
                        ToolCallId = id,
                        Modality = MessageModality.Text
                    });
                    request.Messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        Content = result,
                        ToolCallId = id
                    });
                }

                continue; // Re-call the LLM with tool results
            }

            // Final text response — store with usage stats
            stopwatch.Stop();
            var assistantMessageId = Guid.NewGuid().ToString();
            agentLogic.AddMessage(conversationId, new Message
            {
                Id = assistantMessageId,
                ConversationId = conversationId,
                Role = "assistant",
                Content = fullContent.ToString(),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Modality = MessageModality.Text
            });

            // Re-read conversation — tools may have modified it mid-request (e.g. ActiveSkills)
            var fresh = agentLogic.GetConversation(conversationId) ?? conversation;
            fresh.LastPromptTokens = promptTokens;
            fresh.TotalPromptTokens += promptTokens ?? 0;
            fresh.TotalCompletionTokens += completionTokens ?? 0;
            fresh.TurnCount++;
            fresh.LastActivity = DateTimeOffset.UtcNow;
            agentLogic.UpdateConversation(fresh);

            logger.LogDebug("Conversation {ConversationId}: {PromptTokens} prompt, {CompletionTokens} completion tokens, {ElapsedMs}ms",
                conversationId, promptTokens, completionTokens, stopwatch.ElapsedMilliseconds);
            yield return new AssistantMessageSaved(assistantMessageId);
            yield break;
        }

        logger.LogError("Tool call loop exceeded {MaxRounds} rounds for conversation {ConversationId}",
            maxToolRounds, conversationId);
        throw new InvalidOperationException($"Tool call loop exceeded {maxToolRounds} rounds.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var chatMessages = messages.Select(m => new ChatMessage
        {
            Role = m.Role,
            Content = m.Content
        }).ToList();

        var request = new ChatCompletionRequest
        {
            Messages = chatMessages,
            Stream = true,
            StreamOptions = new StreamOptions { IncludeUsage = true }
        };

        if (options?.ResponseFormat is not null)
            request.ResponseFormat = new ResponseFormatSpec { Type = options.ResponseFormat };

        var url = $"openai/deployments/{model}/chat/completions?api-version={_config.ApiVersion}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };
        var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            logger.LogError("Azure OpenAI returned {StatusCode}: {ErrorBody}", (int)httpResponse.StatusCode, errorBody);
            throw new HttpRequestException($"Azure OpenAI returned {(int)httpResponse.StatusCode}: {errorBody}");
        }

        using var stream = await httpResponse.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            var chunk = JsonSerializer.Deserialize<ChatCompletionResponse>(data);
            var choice = chunk?.Choices?.FirstOrDefault();
            if (choice?.Delta?.Content is { Length: > 0 } content)
                yield return new TextDelta(content);
        }
    }

    private List<ChatMessage> BuildChatMessages(Conversation conversation)
    {
        var chatMessages = new List<ChatMessage>();

        // System prompt
        var systemPrompt = agentLogic.GetSystemPrompt(conversation.Id, conversation.Source, voice: false, conversation.ActiveSkills, conversation.Intention);
        if (!string.IsNullOrEmpty(systemPrompt))
            chatMessages.Add(new ChatMessage { Role = "system", Content = systemPrompt });

        // If the conversation has been compacted, inject the summary as a user message
        // wrapped in <summary> tags. Keeps the real system prompt stable across turns
        // (cache-friendly) while still giving the model visibility into pre-cut history.
        if (!string.IsNullOrEmpty(conversation.Context))
        {
            chatMessages.Add(new ChatMessage
            {
                Role = "user",
                Content = "The conversation history before this point was compacted into the following summary:\n\n"
                          + "<summary>\n" + conversation.Context + "\n</summary>"
            });
        }

        // Reconstruct full message history including tool calls. Opt into blob loading so
        // persisted tool results are inlined as their original full content (not the summary).
        var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);

        // Build channel-message-id -> Message lookup for inline reply-quote rendering.
        // Keyed by ChannelMessageId so we can resolve ReplyToChannelMessageId at render time;
        // the formatter uses Role + CreatedAt as XML attributes on the quote block.
        var channelMessageLookup = new Dictionary<string, Message>();
        foreach (var stored in storedMessages)
        {
            if (stored.ChannelMessageId is { } cmid)
                channelMessageLookup[cmid] = stored;
        }

        for (var i = 0; i < storedMessages.Count; i++)
        {
            var msg = storedMessages[i];

            // Assistant message with tool calls — verify all tool results follow
            if (msg.ToolCalls is not null)
            {
                var toolCalls = JsonSerializer.Deserialize<List<ToolCall>>(msg.ToolCalls);
                if (toolCalls is { Count: > 0 })
                {
                    // Collect the expected tool_call_ids
                    var expectedIds = toolCalls.Select(tc => tc.Id).ToHashSet();

                    // Look ahead for matching tool result messages
                    var foundIds = new HashSet<string>();
                    for (var j = i + 1; j < storedMessages.Count && foundIds.Count < expectedIds.Count; j++)
                    {
                        if (storedMessages[j].Role == "tool" && storedMessages[j].ToolCallId is not null)
                            foundIds.Add(storedMessages[j].ToolCallId!);
                        else
                            break; // Tool results must be contiguous
                    }

                    // Skip this tool call round if incomplete — avoids API 400 errors
                    if (!expectedIds.SetEquals(foundIds))
                    {
                        logger.LogWarning("Skipping orphaned tool call round at message {MessageId}: expected [{Expected}], found [{Found}]",
                            msg.Id, string.Join(", ", expectedIds), string.Join(", ", foundIds));
                        // Skip the assistant message and any partial tool results
                        while (i + 1 < storedMessages.Count && storedMessages[i + 1].Role == "tool")
                            i++;
                        continue;
                    }

                    // Complete round — add assistant message with tool calls
                    chatMessages.Add(new ChatMessage { Role = "assistant", Content = msg.Content, Name = ChannelMessageName(msg), ToolCalls = toolCalls });

                    // Add the matching tool result messages. Prefer the full on-disk content
                    // (loaded via includeToolResultBlobs above); fall back to the compact summary
                    // in Content only for legacy rows or when the blob is missing.
                    foreach (var id in expectedIds)
                    {
                        i++;
                        var toolMsg = storedMessages[i];
                        chatMessages.Add(new ChatMessage
                        {
                            Role = "tool",
                            Content = toolMsg.FullToolResult ?? toolMsg.Content,
                            ToolCallId = toolMsg.ToolCallId
                        });
                    }
                    continue;
                }
            }

            // Regular message (user, assistant text, or tool with id). For tool messages,
            // prefer the full on-disk result loaded via ToolResultRef. For user/assistant
            // messages with a ReplyToChannelMessageId, render an inline blockquote of the
            // replied-to content so the LLM can disambiguate.
            string? content;
            if (msg.Role == "tool" && msg.FullToolResult is not null)
            {
                content = msg.FullToolResult;
            }
            else if (msg.ReplyToChannelMessageId is { } replyId
                     && channelMessageLookup.TryGetValue(replyId, out var quoted))
            {
                content = ReplyQuoteFormatter.Format(msg.Content, quoted);
            }
            else
            {
                content = msg.Content;
            }
            var chatMsg = new ChatMessage { Role = msg.Role, Content = content, Name = ChannelMessageName(msg) };
            if (msg.ToolCallId is not null)
                chatMsg.ToolCallId = msg.ToolCallId;
            chatMessages.Add(chatMsg);
        }

        // Log the full context being sent to the LLM
        foreach (var cm in chatMessages)
            logger.LogDebug("LLM context [{Role}] {Name}: {Content}", cm.Role, cm.Name ?? "-",
                cm.Content?.Length > 200 ? cm.Content[..200] + "..." : cm.Content);

        return chatMessages;
    }

    private List<ChatTool>? BuildTools()
    {
        return agentLogic.Tools.Count > 0
            ? agentLogic.Tools.Select(t => new ChatTool
            {
                Function = new ChatFunction
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.Parameters
                }
            }).ToList()
            : null;
    }

    /// <summary>
    /// Returns a name field for the ChatMessage using the channel message ID (e.g. "msg_123").
    /// The OpenAI API name field is metadata — the LLM sees it but won't echo it in responses.
    /// </summary>
    private static string? ChannelMessageName(Message msg)
    {
        return msg.ChannelMessageId is not null ? $"msg_{msg.ChannelMessageId}" : null;
    }
}
