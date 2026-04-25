using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmText.AnthropicSubscription.Models;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.LlmText.AnthropicSubscription;

/// <summary>
/// LLM text provider that calls the Anthropic Messages API using a setup-token (OAuth bearer)
/// from a Claude subscription. Supports streaming, tool calls, and adaptive thinking.
/// </summary>
public sealed class AnthropicSubscriptionTextProvider(IAgentLogic agentLogic, ILogger<AnthropicSubscriptionTextProvider> logger) : ILlmTextProvider, IDisposable
{
    private AnthropicConfig? _config;
    private HttpClient? _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public const string ProviderKey = "anthropic-subscription";

    public string Key => ProviderKey;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "setupToken", Label = "Setup Token", Type = "Secret", Required = true },
        new() { Key = "models", Label = "Models (comma-separated)", Type = "String", Required = true },
        new() { Key = "maxTokens", Label = "Max Tokens", Type = "String", DefaultValue = "16000" }
    ];

    public IReadOnlyList<string> Models => _config?.Models ?? [];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<AnthropicConfig>(configuration, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.SetupToken))
            throw new InvalidOperationException("setupToken is required.");

        // Parse models from comma-separated string
        if (configuration.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.String)
        {
            _config.Models = modelsProp.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Parse maxTokens from string field
        if (configuration.TryGetProperty("maxTokens", out var maxTokensProp) && maxTokensProp.ValueKind == JsonValueKind.String)
        {
            if (int.TryParse(maxTokensProp.GetString(), out var parsed))
                _config.MaxTokens = parsed;
        }

        _httpClient?.Dispose();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com/"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        // Default headers — Authorization is NOT set here, it must be per-request
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _httpClient.DefaultRequestHeaders.Add("anthropic-beta", "claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14");
        _httpClient.DefaultRequestHeaders.Add("x-app", "cli");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("claude-cli/2.1.91");
        _httpClient.DefaultRequestHeaders.Add("anthropic-dangerous-direct-browser-access", "true");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        logger.LogInformation("Anthropic subscription provider configured with {ModelCount} models, maxTokens={MaxTokens}",
            _config.Models.Length, _config.MaxTokens);
    }

    public void Dispose() => _httpClient?.Dispose();

    /// <inheritdoc />
    public int? GetContextWindow(string model)
    {
        // Anthropic model IDs encode the family; all currently-supported Claude variants expose
        // a 200k-token context window. Unknown models return null; callers fall back to
        // CompactionConfig.MaxContextTokens.
        if (model.Contains("claude", StringComparison.OrdinalIgnoreCase)) return 200_000;
        return null;
    }

    /// <summary>
    /// Detects Anthropic's context-length error. Anthropic returns HTTP 400 with a body
    /// describing the issue — typically "prompt is too long" or a max_tokens reference.
    /// Heuristic — tune as new error shapes appear.
    /// </summary>
    private static bool IsContextOverflow(System.Net.HttpStatusCode status, string body)
    {
        if (status != System.Net.HttpStatusCode.BadRequest) return false;
        return body.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
            || (body.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
                && body.Contains("context", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, Message userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var conversationId = conversation.Id;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogDebug("CompleteAsync called for conversation {ConversationId}", conversationId);

        // Populate the per-conversation context window cache on first turn or after a model
        // switch. Used by the compaction threshold — lets it scale with the active model
        // rather than relying on a global constant.
        if (conversation.ContextWindowTokens is null)
        {
            var window = GetContextWindow(conversation.Model);
            if (window is not null)
                conversation.ContextWindowTokens = window;
        }

        // Persist the caller-supplied user message
        agentLogic.AddMessage(conversationId, userMessage);

        // Build the system prompt blocks and initial message list
        var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type, conversation.ActiveSkills, conversation.Intention);
        var systemBlocks = BuildSystemBlocks(systemPrompt);
        var messages = BuildMessages(conversation);
        var tools = BuildTools();
        var useThinking = conversation.Model.Contains("4-6", StringComparison.OrdinalIgnoreCase);

        // Completion loop (handles tool call rounds, up to 10)
        const int maxToolRounds = 10;
        var overflowRetried = false;
        for (var round = 0; round < maxToolRounds; round++)
        {
            // Inner loop allows a single context-overflow retry: compact + rebuild messages.
            HttpResponseMessage httpResponse;
            while (true)
            {
                var request = new AnthropicMessagesRequest
                {
                    Model = conversation.Model,
                    MaxTokens = _config.MaxTokens,
                    System = systemBlocks,
                    Messages = messages,
                    Tools = tools?.Count > 0 ? tools : null,
                    Stream = true,
                    Thinking = useThinking ? new AnthropicThinking() : null
                };

                // Authorization MUST be per-request — setting on DefaultRequestHeaders causes 429
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
                {
                    Content = JsonContent.Create(request),
                };
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SetupToken);

                httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

                if (httpResponse.IsSuccessStatusCode) break;

                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                if (!overflowRetried && IsContextOverflow(httpResponse.StatusCode, errorBody))
                {
                    overflowRetried = true;
                    logger.LogWarning(
                        "Anthropic context overflow for conversation {ConversationId} — compacting and retrying once",
                        conversationId);

                    httpResponse.Dispose();

                    var compacted = await agentLogic.CompactAsync(conversationId, CompactionReason.Overflow, null, ct);
                    if (!compacted)
                    {
                        throw new HttpRequestException(
                            "Context overflow, and compaction could not reduce history (already minimal or disabled).");
                    }

                    // Rebuild messages from the compacted state.
                    var compactedConv = agentLogic.GetConversation(conversationId) ?? conversation;
                    messages = BuildMessages(compactedConv);
                    continue;
                }

                logger.LogError("Anthropic returned {StatusCode} for conversation {ConversationId}: {ErrorBody}",
                    (int)httpResponse.StatusCode, conversationId, errorBody);
                throw new HttpRequestException($"Anthropic returned {(int)httpResponse.StatusCode}: {errorBody}");
            }

            // Parse SSE stream, accumulating text content and tool call fragments
            var fullContent = new System.Text.StringBuilder();
            var toolCallAccumulator = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
            string? stopReason = null;
            int? inputTokens = null;
            int? outputTokens = null;
            string? currentEventType = null;

            using var stream = await httpResponse.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                // Track the event type from the "event: <type>" line
                if (line.StartsWith("event: "))
                {
                    currentEventType = line["event: ".Length..].Trim();
                    continue;
                }

                if (!line.StartsWith("data: ")) continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]") break;

                // Parse based on the current event type
                switch (currentEventType)
                {
                    case "message_start":
                    {
                        var evt = JsonSerializer.Deserialize<MessageStartEvent>(data, JsonOptions);
                        if (evt?.Message?.Usage is { } usage)
                            inputTokens = usage.InputTokens;
                        break;
                    }

                    case "content_block_start":
                    {
                        // Register new content blocks — tool_use blocks get an accumulator entry
                        var evt = JsonSerializer.Deserialize<ContentBlockStartEvent>(data, JsonOptions);
                        if (evt?.ContentBlock?.Type == "tool_use" && evt.ContentBlock.Id is not null)
                        {
                            toolCallAccumulator[evt.Index] = (
                                evt.ContentBlock.Id,
                                evt.ContentBlock.Name ?? "",
                                new System.Text.StringBuilder()
                            );
                        }
                        break;
                    }

                    case "content_block_delta":
                    {
                        var evt = JsonSerializer.Deserialize<ContentBlockDeltaEvent>(data, JsonOptions);
                        if (evt?.Delta is null) break;

                        if (evt.Delta.Type == "text_delta" && evt.Delta.Text is { Length: > 0 } text)
                        {
                            fullContent.Append(text);
                            yield return new TextDelta(text);
                        }
                        else if (evt.Delta.Type == "input_json_delta" && evt.Delta.PartialJson is not null)
                        {
                            if (toolCallAccumulator.TryGetValue(evt.Index, out var entry))
                                entry.Args.Append(evt.Delta.PartialJson);
                        }
                        break;
                    }

                    case "message_delta":
                    {
                        var evt = JsonSerializer.Deserialize<MessageDeltaEvent>(data, JsonOptions);
                        stopReason = evt?.Delta?.StopReason ?? stopReason;
                        if (evt?.Usage is { } usage)
                            outputTokens = usage.OutputTokens;
                        break;
                    }
                }
            }

            // Tool call round — execute tools and loop
            if (stopReason == "tool_use" && toolCallAccumulator.Count > 0)
            {
                logger.LogDebug("Tool calls requested in conversation {ConversationId}: {ToolNames}",
                    conversationId, string.Join(", ", toolCallAccumulator.Values.Select(t => t.Name)));

                // Build persisted tool call format (same as Azure provider)
                var assembledToolCalls = toolCallAccumulator.OrderBy(kv => kv.Key)
                    .Select(kv => new StoredToolCall
                    {
                        Id = kv.Value.Id,
                        Type = "function",
                        Function = new StoredToolCallFunction
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

                // Add assistant tool_use message to in-memory context
                var toolUseBlocks = assembledToolCalls.Select(tc => new AnthropicContentBlock
                {
                    Type = "tool_use",
                    Id = tc.Id,
                    Name = tc.Function.Name,
                    Input = string.IsNullOrWhiteSpace(tc.Function.Arguments)
                        ? JsonSerializer.Deserialize<JsonElement>("{}")
                        : JsonSerializer.Deserialize<JsonElement>(tc.Function.Arguments)
                }).ToList<AnthropicContentBlock>();
                messages.Add(new AnthropicMessage { Role = "assistant", Content = toolUseBlocks });

                // Execute each tool, yield events, build tool_result blocks for the next user message
                var toolResultBlocks = new List<AnthropicContentBlock>();
                foreach (var (_, (id, name, args)) in toolCallAccumulator.OrderBy(kv => kv.Key))
                {
                    var argsString = args.ToString();
                    yield return new ToolCallEvent(id, name, argsString);

                    logger.LogDebug("Executing tool {ToolName} for conversation {ConversationId}", name, conversationId);
                    var result = await agentLogic.ExecuteToolAsync(conversationId, name, argsString, ct);

                    yield return new ToolResultEvent(id, name, result);

                    // Persist tool result: Content keeps the compact summary (for UI and
                    // backward compat), FullToolResult carries the raw output to disk via the
                    // store's blob writer. Next turn's BuildMessages loads the blob back.
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

                    toolResultBlocks.Add(new AnthropicContentBlock
                    {
                        Type = "tool_result",
                        ToolUseId = id,
                        Content = result
                    });
                }

                // Anthropic requires tool results as a single user message
                messages.Add(new AnthropicMessage { Role = "user", Content = toolResultBlocks });

                continue; // Re-call the LLM with tool results
            }

            // Final text response — persist with usage stats
            stopwatch.Stop();
            var assistantMessageId = Guid.NewGuid().ToString();
            agentLogic.AddMessage(conversationId, new Message
            {
                Id = assistantMessageId,
                ConversationId = conversationId,
                Role = "assistant",
                Content = fullContent.ToString(),
                PromptTokens = inputTokens,
                CompletionTokens = outputTokens,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Modality = MessageModality.Text
            });

            // Re-read conversation — tools may have modified it mid-request (e.g. ActiveSkills)
            var fresh = agentLogic.GetConversation(conversationId) ?? conversation;
            fresh.LastPromptTokens = inputTokens;
            fresh.TotalPromptTokens += inputTokens ?? 0;
            fresh.TotalCompletionTokens += outputTokens ?? 0;
            fresh.TurnCount++;
            fresh.LastActivity = DateTimeOffset.UtcNow;
            agentLogic.UpdateConversation(fresh);

            logger.LogDebug("Conversation {ConversationId}: {InputTokens} input, {OutputTokens} output tokens, {ElapsedMs}ms",
                conversationId, inputTokens, outputTokens, stopwatch.ElapsedMilliseconds);
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

        // Convert to Anthropic messages — simple user/assistant alternation, no tools
        var anthropicMessages = messages
            .Where(m => m.Role != "system")
            .Select(m => new AnthropicMessage { Role = m.Role == "tool" ? "user" : m.Role, Content = m.Content ?? "" })
            .ToList();

        // Use first system message as system prompt, or empty
        var systemText = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        var systemBlocks = BuildSystemBlocks(systemText);

        var useThinking = model.Contains("4-6", StringComparison.OrdinalIgnoreCase);
        var request = new AnthropicMessagesRequest
        {
            Model = model,
            MaxTokens = _config.MaxTokens,
            System = systemBlocks,
            Messages = anthropicMessages,
            Stream = true,
            Thinking = useThinking ? new AnthropicThinking() : null
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SetupToken);

        var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            logger.LogError("Anthropic returned {StatusCode}: {ErrorBody}", (int)httpResponse.StatusCode, errorBody);
            throw new HttpRequestException($"Anthropic returned {(int)httpResponse.StatusCode}: {errorBody}");
        }

        using var stream = await httpResponse.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        string? currentEventType = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event: "))
            {
                currentEventType = line["event: ".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            if (currentEventType == "content_block_delta")
            {
                var evt = JsonSerializer.Deserialize<ContentBlockDeltaEvent>(data, JsonOptions);
                if (evt?.Delta?.Type == "text_delta" && evt.Delta.Text is { Length: > 0 } text)
                    yield return new TextDelta(text);
            }
        }
    }

    /// <summary>
    /// Builds the two-block system prompt array required by Anthropic.
    /// The first block identifies the agent as Claude Code CLI; the second carries the actual system prompt.
    /// </summary>
    private static List<AnthropicTextBlock> BuildSystemBlocks(string systemPrompt)
    {
        var blocks = new List<AnthropicTextBlock>
        {
            new() { Text = "You are Claude Code, Anthropic's official CLI for Claude." }
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            blocks.Add(new AnthropicTextBlock { Text = systemPrompt });

        return blocks;
    }

    /// <summary>
    /// Reconstructs the Anthropic message list from stored conversation history.
    /// Handles tool call rounds, orphaned call skipping, and the Anthropic tool_result user message convention.
    /// </summary>
    private List<AnthropicMessage> BuildMessages(Conversation conversation)
    {
        var result = new List<AnthropicMessage>();

        // If the conversation has been compacted, inject the summary as a user message
        // wrapped in <summary> tags. Keeps the real system prompt (system blocks) stable
        // across turns — Anthropic prompt caching is strict about system prefix changes.
        if (!string.IsNullOrEmpty(conversation.Context))
        {
            result.Add(new AnthropicMessage
            {
                Role = "user",
                Content = "The conversation history before this point was compacted into the following summary:\n\n"
                          + "<summary>\n" + conversation.Context + "\n</summary>"
            });
        }

        // Opt into blob loading so persisted tool results are inlined as their original full
        // content (not the compact summary in Content).
        var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);

        // Build channel-message-id -> content lookup for inline reply-quote rendering.
        // Keyed by ChannelMessageId so we can resolve ReplyToChannelMessageId at render time.
        var channelMessageContent = new Dictionary<string, string?>();
        foreach (var stored in storedMessages)
        {
            if (stored.ChannelMessageId is { } cmid)
                channelMessageContent[cmid] = stored.Content;
        }

        for (var i = 0; i < storedMessages.Count; i++)
        {
            var msg = storedMessages[i];

            // Skip standalone tool result messages — handled as part of their tool call round
            if (msg.Role == "tool")
                continue;

            // Assistant message with tool calls
            if (msg.ToolCalls is not null)
            {
                var toolCalls = JsonSerializer.Deserialize<List<StoredToolCall>>(msg.ToolCalls, JsonOptions);
                if (toolCalls is { Count: > 0 })
                {
                    var expectedIds = toolCalls.Select(tc => tc.Id).ToHashSet();

                    // Look ahead for matching tool result messages
                    var foundIds = new HashSet<string>();
                    for (var j = i + 1; j < storedMessages.Count && foundIds.Count < expectedIds.Count; j++)
                    {
                        if (storedMessages[j].Role == "tool" && storedMessages[j].ToolCallId is not null)
                            foundIds.Add(storedMessages[j].ToolCallId!);
                        else
                            break;
                    }

                    // Skip orphaned tool call round — avoids API errors
                    if (!expectedIds.SetEquals(foundIds))
                    {
                        logger.LogWarning("Skipping orphaned tool call round at message {MessageId}: expected [{Expected}], found [{Found}]",
                            msg.Id, string.Join(", ", expectedIds), string.Join(", ", foundIds));
                        while (i + 1 < storedMessages.Count && storedMessages[i + 1].Role == "tool")
                            i++;
                        continue;
                    }

                    // Add assistant message with tool_use blocks
                    var toolUseBlocks = toolCalls.Select(tc => new AnthropicContentBlock
                    {
                        Type = "tool_use",
                        Id = tc.Id,
                        Name = tc.Function.Name,
                        Input = string.IsNullOrWhiteSpace(tc.Function.Arguments)
                        ? JsonSerializer.Deserialize<JsonElement>("{}")
                        : JsonSerializer.Deserialize<JsonElement>(tc.Function.Arguments)
                    }).ToList<AnthropicContentBlock>();
                    result.Add(new AnthropicMessage { Role = "assistant", Content = toolUseBlocks });

                    // Collect tool results in a single user message (Anthropic convention).
                    // Prefer the full on-disk content; fall back to the compact summary in
                    // Content only for legacy rows or when the blob is missing.
                    var toolResultBlocks = new List<AnthropicContentBlock>();
                    foreach (var id in expectedIds)
                    {
                        i++;
                        var toolMsg = storedMessages[i];
                        toolResultBlocks.Add(new AnthropicContentBlock
                        {
                            Type = "tool_result",
                            ToolUseId = toolMsg.ToolCallId,
                            Content = toolMsg.FullToolResult ?? toolMsg.Content
                        });
                    }
                    result.Add(new AnthropicMessage { Role = "user", Content = toolResultBlocks });
                    continue;
                }
            }

            // Regular user or assistant message. When ReplyToChannelMessageId resolves to
            // a known earlier message, render an inline blockquote so the LLM can
            // disambiguate which earlier message is being replied to.
            string content;
            if (msg.ReplyToChannelMessageId is { } replyId
                && channelMessageContent.TryGetValue(replyId, out var quoted))
            {
                content = ReplyQuoteFormatter.Format(msg.Content, quoted);
            }
            else
            {
                content = msg.Content ?? "";
            }
            result.Add(new AnthropicMessage { Role = msg.Role, Content = content });
        }

        foreach (var m in result)
            logger.LogDebug("LLM context [{Role}]: {Content}", m.Role,
                m.Content is string s ? (s.Length > 200 ? s[..200] + "..." : s) : "[blocks]");

        return result;
    }

    /// <summary>
    /// Converts registered agent tools to Anthropic tool definitions (input_schema vs OpenAI's parameters).
    /// </summary>
    private List<AnthropicToolDefinition>? BuildTools()
    {
        return agentLogic.Tools.Count > 0
            ? agentLogic.Tools.Select(t => new AnthropicToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.Parameters
            }).ToList()
            : null;
    }
}

/// <summary>
/// Stored representation of a tool call, matching the format used by AzureOpenAiTextProvider.
/// </summary>
internal sealed class StoredToolCall
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [System.Text.Json.Serialization.JsonPropertyName("function")]
    public StoredToolCallFunction Function { get; set; } = new();
}

/// <summary>
/// The function portion of a stored tool call.
/// </summary>
internal sealed class StoredToolCallFunction
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";
}
