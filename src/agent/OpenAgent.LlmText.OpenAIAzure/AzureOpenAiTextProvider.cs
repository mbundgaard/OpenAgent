using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmText.OpenAIAzure.Models;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.LlmText.OpenAIAzure;

public sealed class AzureOpenAiTextProvider(IAgentLogic agentLogic, ILogger<AzureOpenAiTextProvider> logger) : ILlmTextProvider, IDisposable
{
    private AzureOpenAiTextConfig? _config;
    private HttpClient? _httpClient;

    public const string ProviderKey = "azure-openai-text";

    public string Key => ProviderKey;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
        new() { Key = "endpoint", Label = "Endpoint", Type = "String", Required = true },
        new() { Key = "models", Label = "Models (comma-separated)", Type = "String", Required = true },
        new() { Key = "apiVersion", Label = "API Version", Type = "String", DefaultValue = "2025-04-01-preview" }
    ];

    public IReadOnlyList<string> Models => _config?.Models ?? [];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<AzureOpenAiTextConfig>(configuration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(_config.Endpoint))
            throw new InvalidOperationException("endpoint is required.");

        // Parse models from comma-separated string if provided as a single string
        if (configuration.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.String)
        {
            _config.Models = modelsProp.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var baseUri = _config.Endpoint.TrimEnd('/');
        _httpClient?.Dispose();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUri + "/")
        };
        _httpClient.DefaultRequestHeaders.Add("api-key", _config.ApiKey);

        logger.LogInformation("Text provider configured with {ModelCount} models at {Endpoint}",
            _config.Models.Length, _config.Endpoint);
    }

    public void Dispose() => _httpClient?.Dispose();

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, Message userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var conversationId = conversation.Id;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogDebug("StreamAsync called for conversation {ConversationId}", conversationId);

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

        var url = $"openai/deployments/{conversation.Model}/chat/completions?api-version={_config.ApiVersion}";

        // Completion loop (handles tool calls across streaming rounds)
        const int maxToolRounds = 10;
        for (var round = 0; round < maxToolRounds; round++)
        {

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(request)
            };
            var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
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
                    ToolCalls = JsonSerializer.Serialize(assembledToolCalls)
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

                    // Persist tool result summary (compact), keep full result in-memory for current turn
                    agentLogic.AddMessage(conversationId, new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = conversationId,
                        Role = "tool",
                        Content = ToolResultSummary.Create(name, result),
                        ToolCallId = id
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
                ElapsedMs = stopwatch.ElapsedMilliseconds
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
        var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type, conversation.ActiveSkills);
        if (!string.IsNullOrEmpty(systemPrompt))
            chatMessages.Add(new ChatMessage { Role = "system", Content = systemPrompt });

        // Reconstruct full message history including tool calls
        var storedMessages = agentLogic.GetMessages(conversation.Id);
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

                    // Add the matching tool result messages
                    foreach (var id in expectedIds)
                    {
                        i++;
                        chatMessages.Add(new ChatMessage
                        {
                            Role = "tool",
                            Content = storedMessages[i].Content,
                            ToolCallId = storedMessages[i].ToolCallId
                        });
                    }
                    continue;
                }
            }

            // Regular message (user, assistant text, or tool with id)
            var content = msg.ReplyToChannelMessageId is not null
                ? $"[Reply to Msg: {msg.ReplyToChannelMessageId}] {msg.Content}"
                : msg.Content;
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
