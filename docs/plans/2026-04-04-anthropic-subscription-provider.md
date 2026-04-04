# Anthropic Subscription Text Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an `ILlmTextProvider` that calls the Anthropic Messages API using a Claude subscription setup-token (OAuth), with full streaming and tool call support.

**Architecture:** New project `OpenAgent.LlmText.AnthropicSubscription` following the same pattern as `OpenAgent.LlmText.OpenAIAzure`. Implements `ILlmTextProvider` + `IConfigurable`, registered as a keyed singleton. Uses raw `HttpClient` with Anthropic SSE streaming. Maps Anthropic's `tool_use`/`tool_result` content blocks to the existing `CompletionEvent` flow.

**Tech Stack:** .NET 10, `System.Text.Json`, `HttpClient`, `IAsyncEnumerable<CompletionEvent>`

---

## File Structure

```
src/agent/OpenAgent.LlmText.AnthropicSubscription/
  OpenAgent.LlmText.AnthropicSubscription.csproj   -- project file, refs Contracts + Models
  AnthropicSubscriptionTextProvider.cs              -- main provider (ILlmTextProvider)
  Models/
    AnthropicConfig.cs                              -- config POCO
    AnthropicRequest.cs                             -- Messages API request models
    AnthropicResponse.cs                            -- non-streaming response + SSE event models

Modify:
  src/agent/OpenAgent/OpenAgent.csproj              -- add ProjectReference
  src/agent/OpenAgent/Program.cs                    -- register keyed singleton + IConfigurable
  src/agent/OpenAgent.sln                           -- add project to solution
```

---

### Task 1: Create project and config model

**Files:**
- Create: `src/agent/OpenAgent.LlmText.AnthropicSubscription/OpenAgent.LlmText.AnthropicSubscription.csproj`
- Create: `src/agent/OpenAgent.LlmText.AnthropicSubscription/Models/AnthropicConfig.cs`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add to solution**

```bash
cd src/agent && dotnet sln add OpenAgent.LlmText.AnthropicSubscription/OpenAgent.LlmText.AnthropicSubscription.csproj
```

- [ ] **Step 3: Create AnthropicConfig.cs**

```csharp
namespace OpenAgent.LlmText.AnthropicSubscription.Models;

internal sealed class AnthropicConfig
{
    public string SetupToken { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Models { get; set; } = [];
    public int MaxTokens { get; set; } = 16000;
}
```

- [ ] **Step 4: Build to verify**

```bash
cd src/agent && dotnet build OpenAgent.LlmText.AnthropicSubscription/OpenAgent.LlmText.AnthropicSubscription.csproj
```
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.LlmText.AnthropicSubscription/ src/agent/OpenAgent.sln
git commit -m "feat: add AnthropicSubscription project with config model"
```

---

### Task 2: Create Anthropic request models

**Files:**
- Create: `src/agent/OpenAgent.LlmText.AnthropicSubscription/Models/AnthropicRequest.cs`

The Anthropic Messages API has a different shape than OpenAI. Key differences:
- System prompt is a top-level `system` field (array of text blocks), not a message
- Tool definitions use `input_schema` instead of `parameters`
- Messages use `content` as an array of content blocks (text, tool_use, tool_result)
- Tool results are sent as `role: "user"` with `tool_result` content blocks

- [ ] **Step 1: Create AnthropicRequest.cs**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.AnthropicSubscription.Models;

internal sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 16000;

    [JsonPropertyName("system")]
    public required List<AnthropicTextBlock> System { get; set; }

    [JsonPropertyName("messages")]
    public required List<AnthropicMessage> Messages { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnthropicToolDefinition>? Tools { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicThinking? Thinking { get; set; }
}

internal sealed class AnthropicTextBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

internal sealed class AnthropicThinking
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "adaptive";
}

internal sealed class AnthropicMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public required object Content { get; set; } // string or List<AnthropicContentBlock>
}

/// <summary>
/// A content block in a message — text, tool_use, or tool_result.
/// Uses a single class with nullable fields to simplify serialization.
/// </summary>
internal sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    // text block
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    // tool_use block
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Input { get; set; }

    // tool_result block
    [JsonPropertyName("tool_use_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolUseId { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }
}

internal sealed class AnthropicToolDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public required object InputSchema { get; set; }
}
```

- [ ] **Step 2: Build to verify**

```bash
cd src/agent && dotnet build OpenAgent.LlmText.AnthropicSubscription/OpenAgent.LlmText.AnthropicSubscription.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.LlmText.AnthropicSubscription/Models/AnthropicRequest.cs
git commit -m "feat: add Anthropic Messages API request models"
```

---

### Task 3: Create Anthropic response/SSE models

**Files:**
- Create: `src/agent/OpenAgent.LlmText.AnthropicSubscription/Models/AnthropicResponse.cs`

Anthropic SSE events have a typed `event:` line followed by a `data:` JSON line. Event types:
- `message_start` — contains the full `Message` object with `id`, `model`, `usage` (input tokens)
- `content_block_start` — starts a new content block (text or tool_use), includes `index` and block stub
- `content_block_delta` — incremental data for a block (text delta or tool input JSON delta)
- `content_block_stop` — marks a content block as complete
- `message_delta` — contains `stop_reason` and output token `usage`
- `message_stop` — stream is done

- [ ] **Step 1: Create AnthropicResponse.cs**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.AnthropicSubscription.Models;

/// <summary>
/// Wraps a single SSE event from the Anthropic streaming API.
/// The event type determines which fields are populated.
/// </summary>
internal sealed class AnthropicStreamEvent
{
    public required string EventType { get; set; }
    public JsonElement Data { get; set; }
}

// message_start: { "type": "message_start", "message": { ... } }
internal sealed class MessageStartEvent
{
    [JsonPropertyName("message")]
    public AnthropicResponseMessage? Message { get; set; }
}

internal sealed class AnthropicResponseMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

internal sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

// content_block_start: { "type": "content_block_start", "index": 0, "content_block": { "type": "text", "text": "" } }
// content_block_start: { "type": "content_block_start", "index": 1, "content_block": { "type": "tool_use", "id": "...", "name": "...", "input": {} } }
internal sealed class ContentBlockStartEvent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("content_block")]
    public ContentBlockStub? ContentBlock { get; set; }
}

internal sealed class ContentBlockStub
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

// content_block_delta: { "type": "content_block_delta", "index": 0, "delta": { "type": "text_delta", "text": "Hello" } }
// content_block_delta: { "type": "content_block_delta", "index": 1, "delta": { "type": "input_json_delta", "partial_json": "{\"q" } }
internal sealed class ContentBlockDeltaEvent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public DeltaPayload? Delta { get; set; }
}

internal sealed class DeltaPayload
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    // text_delta
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    // input_json_delta
    [JsonPropertyName("partial_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialJson { get; set; }
}

// message_delta: { "type": "message_delta", "delta": { "stop_reason": "end_turn" }, "usage": { "output_tokens": 42 } }
internal sealed class MessageDeltaEvent
{
    [JsonPropertyName("delta")]
    public MessageDeltaPayload? Delta { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

internal sealed class MessageDeltaPayload
{
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }
}
```

- [ ] **Step 2: Build to verify**

```bash
cd src/agent && dotnet build OpenAgent.LlmText.AnthropicSubscription/OpenAgent.LlmText.AnthropicSubscription.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.LlmText.AnthropicSubscription/Models/AnthropicResponse.cs
git commit -m "feat: add Anthropic SSE response models"
```

---

### Task 4: Implement the provider — Configure, BuildSystemPrompt, BuildMessages, BuildTools

**Files:**
- Create: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`

This task creates the provider class with configuration, message building, and tool mapping. The streaming `CompleteAsync` is added in the next task.

Key Anthropic-specific behaviors:
1. **Per-request Authorization header** — never set on `DefaultRequestHeaders` (causes instant 429)
2. **Identity headers** on `DefaultRequestHeaders`: `anthropic-version`, `anthropic-beta`, `x-app`, `user-agent`, `anthropic-dangerous-direct-browser-access`
3. **System prompt** must be a text block array with Claude Code identity prefix as the first block
4. **Adaptive thinking** for 4.6 models — no temperature param
5. **Tool definitions** use `input_schema` (not `parameters`)
6. **Tool results** are `role: "user"` messages with `tool_result` content blocks (not `role: "tool"`)
7. **Assistant messages with tool calls** use `tool_use` content blocks in the `content` array

- [ ] **Step 1: Create the provider class with config and message building**

```csharp
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmText.AnthropicSubscription.Models;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.LlmText.AnthropicSubscription;

/// <summary>
/// Text completion provider that calls the Anthropic Messages API using a Claude subscription
/// setup-token (OAuth). Supports streaming and tool calls.
/// </summary>
public sealed class AnthropicSubscriptionTextProvider(
    IAgentLogic agentLogic,
    ILogger<AnthropicSubscriptionTextProvider> logger) : ILlmTextProvider, IDisposable
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ClaudeCodeIdentity = "You are Claude Code, Anthropic's official CLI for Claude.";

    private AnthropicConfig? _config;
    private HttpClient? _httpClient;

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
        _config = JsonSerializer.Deserialize<AnthropicConfig>(configuration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.SetupToken))
            throw new InvalidOperationException("setupToken is required.");

        // Parse models from comma-separated string
        if (configuration.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.String)
        {
            _config.Models = modelsProp.GetString()!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Parse maxTokens from string
        if (configuration.TryGetProperty("maxTokens", out var maxTokensProp) && maxTokensProp.ValueKind == JsonValueKind.String)
        {
            if (int.TryParse(maxTokensProp.GetString(), out var maxTokens))
                _config.MaxTokens = maxTokens;
        }

        // Create HttpClient with default headers (everything except Authorization)
        _httpClient?.Dispose();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _httpClient.DefaultRequestHeaders.Add("anthropic-beta",
            "claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14");
        _httpClient.DefaultRequestHeaders.Add("x-app", "cli");
        _httpClient.DefaultRequestHeaders.Add("anthropic-dangerous-direct-browser-access", "true");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("claude-cli/2.1.91");

        logger.LogInformation("Anthropic subscription provider configured with {ModelCount} models, max_tokens={MaxTokens}",
            _config.Models.Length, _config.MaxTokens);
    }

    public void Dispose() => _httpClient?.Dispose();

    /// <summary>
    /// Builds the Anthropic system prompt as a text block array.
    /// First block is the Claude Code identity (required for OAuth).
    /// Second block is the actual agent system prompt.
    /// </summary>
    private List<AnthropicTextBlock> BuildSystemPrompt(Conversation conversation)
    {
        var blocks = new List<AnthropicTextBlock>
        {
            new() { Text = ClaudeCodeIdentity }
        };

        var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type);
        if (!string.IsNullOrEmpty(systemPrompt))
            blocks.Add(new AnthropicTextBlock { Text = systemPrompt });

        return blocks;
    }

    /// <summary>
    /// Converts stored messages to Anthropic format. Key differences from OpenAI:
    /// - Tool calls from assistant are content blocks (type: tool_use) in the assistant message
    /// - Tool results are content blocks (type: tool_result) in a user message
    /// - No "system" or "tool" role — system is top-level, tool results are user messages
    /// </summary>
    private List<AnthropicMessage> BuildMessages(Conversation conversation)
    {
        var messages = new List<AnthropicMessage>();
        var storedMessages = agentLogic.GetMessages(conversation.Id);

        for (var i = 0; i < storedMessages.Count; i++)
        {
            var msg = storedMessages[i];

            // Assistant message with tool calls
            if (msg.ToolCalls is not null)
            {
                var toolCalls = JsonSerializer.Deserialize<List<StoredToolCall>>(msg.ToolCalls);
                if (toolCalls is { Count: > 0 })
                {
                    // Validate all tool results follow (same orphan check as Azure provider)
                    var expectedIds = toolCalls.Select(tc => tc.Id).ToHashSet();
                    var foundIds = new HashSet<string>();
                    for (var j = i + 1; j < storedMessages.Count && foundIds.Count < expectedIds.Count; j++)
                    {
                        if (storedMessages[j].Role == "tool" && storedMessages[j].ToolCallId is not null)
                            foundIds.Add(storedMessages[j].ToolCallId!);
                        else
                            break;
                    }

                    if (!expectedIds.SetEquals(foundIds))
                    {
                        logger.LogWarning("Skipping orphaned tool call round at message {MessageId}", msg.Id);
                        while (i + 1 < storedMessages.Count && storedMessages[i + 1].Role == "tool")
                            i++;
                        continue;
                    }

                    // Build assistant message with tool_use content blocks
                    var contentBlocks = new List<AnthropicContentBlock>();
                    if (!string.IsNullOrEmpty(msg.Content))
                        contentBlocks.Add(new AnthropicContentBlock { Type = "text", Text = msg.Content });

                    foreach (var tc in toolCalls)
                    {
                        contentBlocks.Add(new AnthropicContentBlock
                        {
                            Type = "tool_use",
                            Id = tc.Id,
                            Name = tc.Function?.Name,
                            Input = JsonSerializer.Deserialize<JsonElement>(tc.Function?.Arguments ?? "{}")
                        });
                    }

                    messages.Add(new AnthropicMessage { Role = "assistant", Content = contentBlocks });

                    // Build user message with tool_result content blocks
                    var resultBlocks = new List<AnthropicContentBlock>();
                    foreach (var _ in expectedIds)
                    {
                        i++;
                        resultBlocks.Add(new AnthropicContentBlock
                        {
                            Type = "tool_result",
                            ToolUseId = storedMessages[i].ToolCallId,
                            Content = storedMessages[i].Content
                        });
                    }
                    messages.Add(new AnthropicMessage { Role = "user", Content = resultBlocks });
                    continue;
                }
            }

            // Skip tool role messages (already handled above as tool_result blocks)
            if (msg.Role == "tool") continue;

            // Regular user or assistant message
            var content = msg.ReplyToChannelMessageId is not null
                ? $"[Reply to Msg: {msg.ReplyToChannelMessageId}] {msg.Content}"
                : msg.Content ?? "";
            messages.Add(new AnthropicMessage { Role = msg.Role, Content = content });
        }

        return messages;
    }

    /// <summary>
    /// Converts agent tool definitions to Anthropic format (input_schema instead of parameters).
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

    // CompleteAsync methods added in next task — placeholder to satisfy interface
    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, Message userMessage, CancellationToken ct = default)
        => throw new NotImplementedException("Added in next task");

    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages, string model,
        CompletionOptions? options = null, CancellationToken ct = default)
        => throw new NotImplementedException("Added in next task");
}

/// <summary>
/// Matches the ToolCall shape persisted by the Azure provider so we can deserialize stored tool calls.
/// </summary>
internal sealed class StoredToolCall
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("function")]
    public StoredToolCallFunction? Function { get; set; }
}

internal sealed class StoredToolCallFunction
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}
```

- [ ] **Step 2: Build to verify**

```bash
cd src/agent && dotnet build OpenAgent.LlmText.AnthropicSubscription/OpenAgent.LlmText.AnthropicSubscription.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs
git commit -m "feat: add Anthropic provider with config, message building, and tool mapping"
```

---

### Task 5: Implement streaming CompleteAsync (conversation overload)

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`

This is the core streaming implementation. Anthropic SSE is event-typed (unlike OpenAI which is all `data:` lines). Each SSE frame has an `event:` line followed by a `data:` line.

The streaming loop accumulates:
- Text content from `content_block_delta` (type: `text_delta`)
- Tool use IDs and names from `content_block_start` (type: `tool_use`)
- Tool arguments from `content_block_delta` (type: `input_json_delta`)
- Stop reason from `message_delta`
- Token usage from `message_start` (input tokens) and `message_delta` (output tokens)

When `stop_reason` is `"tool_use"`, execute the tools and loop (same pattern as Azure provider).

- [ ] **Step 1: Replace the two placeholder CompleteAsync methods with full implementations**

Remove the two `throw new NotImplementedException` methods and replace with:

```csharp
    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, Message userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var conversationId = conversation.Id;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogDebug("CompleteAsync called for conversation {ConversationId}", conversationId);

        // Persist the caller-supplied user message
        agentLogic.AddMessage(conversationId, userMessage);

        // Build request components — messages list is mutated across tool call rounds
        var systemBlocks = BuildSystemPrompt(conversation);
        var anthropicMessages = BuildMessages(conversation);
        var tools = BuildTools();

        // Determine if model supports adaptive thinking (4.6 models)
        var useAdaptiveThinking = conversation.Model.Contains("4-6");

        const int maxToolRounds = 10;
        int? inputTokens = null;
        int? outputTokens = null;

        for (var round = 0; round < maxToolRounds; round++)
        {
            var requestBody = new AnthropicMessagesRequest
            {
                Model = conversation.Model,
                MaxTokens = _config.MaxTokens,
                System = systemBlocks,
                Messages = anthropicMessages,
                Tools = tools,
                Stream = true,
                Thinking = useAdaptiveThinking ? new AnthropicThinking() : null
            };

            // Authorization MUST be set per-request, not on DefaultRequestHeaders
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SetupToken);
            httpRequest.Content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                logger.LogError("Anthropic returned {StatusCode} for conversation {ConversationId}: {ErrorBody}",
                    (int)httpResponse.StatusCode, conversationId, errorBody);
                throw new HttpRequestException(
                    $"Anthropic returned {(int)httpResponse.StatusCode}: {errorBody}");
            }

            // Parse SSE stream
            var fullContent = new StringBuilder();
            var toolUseBlocks = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
            string? stopReason = null;

            using var stream = await httpResponse.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? currentEventType = null;

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                // SSE format: "event: <type>" followed by "data: <json>"
                if (line.StartsWith("event: "))
                {
                    currentEventType = line["event: ".Length..].Trim();
                    continue;
                }

                if (!line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..];

                switch (currentEventType)
                {
                    case "message_start":
                    {
                        var evt = JsonSerializer.Deserialize<MessageStartEvent>(data);
                        if (evt?.Message?.Usage is not null)
                            inputTokens = evt.Message.Usage.InputTokens;
                        break;
                    }

                    case "content_block_start":
                    {
                        var evt = JsonSerializer.Deserialize<ContentBlockStartEvent>(data);
                        if (evt?.ContentBlock?.Type == "tool_use")
                        {
                            toolUseBlocks[evt.Index] = (
                                evt.ContentBlock.Id ?? "",
                                evt.ContentBlock.Name ?? "",
                                new StringBuilder());
                        }
                        break;
                    }

                    case "content_block_delta":
                    {
                        var evt = JsonSerializer.Deserialize<ContentBlockDeltaEvent>(data);
                        if (evt?.Delta is null) break;

                        if (evt.Delta.Type == "text_delta" && evt.Delta.Text is not null)
                        {
                            fullContent.Append(evt.Delta.Text);
                            yield return new TextDelta(evt.Delta.Text);
                        }
                        else if (evt.Delta.Type == "input_json_delta" && evt.Delta.PartialJson is not null)
                        {
                            if (toolUseBlocks.TryGetValue(evt.Index, out var entry))
                                entry.Args.Append(evt.Delta.PartialJson);
                        }
                        break;
                    }

                    case "message_delta":
                    {
                        var evt = JsonSerializer.Deserialize<MessageDeltaEvent>(data);
                        stopReason = evt?.Delta?.StopReason ?? stopReason;
                        if (evt?.Usage is not null)
                            outputTokens = evt.Usage.OutputTokens;
                        break;
                    }
                }

                currentEventType = null;
            }

            // If the model requested tool calls, execute them and loop
            if (stopReason == "tool_use" && toolUseBlocks.Count > 0)
            {
                logger.LogDebug("Tool calls requested in conversation {ConversationId}: {ToolNames}",
                    conversationId, string.Join(", ", toolUseBlocks.Values.Select(t => t.Name)));

                // Build assistant message content blocks for persistence and wire
                var assistantBlocks = new List<AnthropicContentBlock>();
                if (fullContent.Length > 0)
                    assistantBlocks.Add(new AnthropicContentBlock { Type = "text", Text = fullContent.ToString() });

                // Build stored tool calls (same format as Azure provider for persistence compatibility)
                var storedToolCalls = new List<object>();
                foreach (var (_, (toolId, toolName, toolArgs)) in toolUseBlocks.OrderBy(kv => kv.Key))
                {
                    var argsString = toolArgs.ToString();
                    assistantBlocks.Add(new AnthropicContentBlock
                    {
                        Type = "tool_use",
                        Id = toolId,
                        Name = toolName,
                        Input = JsonSerializer.Deserialize<JsonElement>(
                            string.IsNullOrEmpty(argsString) ? "{}" : argsString)
                    });
                    storedToolCalls.Add(new { id = toolId, type = "function",
                        function = new { name = toolName, arguments = argsString } });
                }

                // Persist assistant message with tool calls
                agentLogic.AddMessage(conversationId, new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    ConversationId = conversationId,
                    Role = "assistant",
                    Content = fullContent.Length > 0 ? fullContent.ToString() : null,
                    ToolCalls = JsonSerializer.Serialize(storedToolCalls)
                });
                anthropicMessages.Add(new AnthropicMessage { Role = "assistant", Content = assistantBlocks });

                // Execute each tool, yield events, persist results, build wire message
                var toolResultBlocks = new List<AnthropicContentBlock>();
                foreach (var (_, (toolId, toolName, toolArgs)) in toolUseBlocks.OrderBy(kv => kv.Key))
                {
                    var argsString = toolArgs.ToString();
                    yield return new ToolCallEvent(toolId, toolName, argsString);

                    logger.LogDebug("Executing tool {ToolName} for conversation {ConversationId}",
                        toolName, conversationId);
                    var result = await agentLogic.ExecuteToolAsync(conversationId, toolName, argsString, ct);

                    yield return new ToolResultEvent(toolId, toolName, result);

                    agentLogic.AddMessage(conversationId, new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = conversationId,
                        Role = "tool",
                        Content = ToolResultSummary.Create(toolName, result),
                        ToolCallId = toolId
                    });

                    toolResultBlocks.Add(new AnthropicContentBlock
                    {
                        Type = "tool_result",
                        ToolUseId = toolId,
                        Content = result
                    });
                }

                // Tool results are sent as a user message in Anthropic's format
                anthropicMessages.Add(new AnthropicMessage { Role = "user", Content = toolResultBlocks });

                fullContent.Clear();
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
                PromptTokens = inputTokens,
                CompletionTokens = outputTokens,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            });

            conversation.LastPromptTokens = inputTokens;
            conversation.TotalPromptTokens += inputTokens ?? 0;
            conversation.TotalCompletionTokens += outputTokens ?? 0;
            conversation.TurnCount++;
            conversation.LastActivity = DateTimeOffset.UtcNow;
            agentLogic.UpdateConversation(conversation);

            logger.LogDebug("Conversation {ConversationId}: {InputTokens} input, {OutputTokens} output tokens, {ElapsedMs}ms",
                conversationId, inputTokens, outputTokens, stopwatch.ElapsedMilliseconds);
            yield return new AssistantMessageSaved(assistantMessageId);
            yield break;
        }

        logger.LogError("Tool call loop exceeded {MaxRounds} rounds for conversation {ConversationId}",
            maxToolRounds, conversationId);
        throw new InvalidOperationException($"Tool call loop exceeded {maxToolRounds} rounds.");
    }
```

- [ ] **Step 2: Build to verify**

```bash
cd src/agent && dotnet build OpenAgent.LlmText.AnthropicSubscription/OpenAgent.LlmText.AnthropicSubscription.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs
git commit -m "feat: implement streaming CompleteAsync with tool call loop for Anthropic"
```

---

### Task 6: Implement raw CompleteAsync (non-conversation overload)

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`

This overload is used by compaction and other non-conversation callers. No tool calls, no persistence. Just streams text deltas.

- [ ] **Step 1: Replace the raw CompleteAsync placeholder**

```csharp
    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages, string model,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var anthropicMessages = messages.Select(m => new AnthropicMessage
        {
            Role = m.Role,
            Content = m.Content ?? ""
        }).ToList();

        var useAdaptiveThinking = model.Contains("4-6");

        var requestBody = new AnthropicMessagesRequest
        {
            Model = model,
            MaxTokens = _config.MaxTokens,
            System = [new AnthropicTextBlock { Text = ClaudeCodeIdentity }],
            Messages = anthropicMessages,
            Stream = true,
            Thinking = useAdaptiveThinking ? new AnthropicThinking() : null
        };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SetupToken);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            logger.LogError("Anthropic returned {StatusCode}: {ErrorBody}", (int)httpResponse.StatusCode, errorBody);
            throw new HttpRequestException($"Anthropic returned {(int)httpResponse.StatusCode}: {errorBody}");
        }

        using var stream = await httpResponse.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

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

            if (currentEventType == "content_block_delta")
            {
                var evt = JsonSerializer.Deserialize<ContentBlockDeltaEvent>(data);
                if (evt?.Delta?.Type == "text_delta" && evt.Delta.Text is not null)
                    yield return new TextDelta(evt.Delta.Text);
            }

            currentEventType = null;
        }
    }
```

- [ ] **Step 2: Build to verify**

```bash
cd src/agent && dotnet build OpenAgent.LlmText.AnthropicSubscription/OpenAgent.LlmText.AnthropicSubscription.csproj
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs
git commit -m "feat: implement raw CompleteAsync for compaction/non-conversation callers"
```

---

### Task 7: Wire into DI and solution

**Files:**
- Modify: `src/agent/OpenAgent/OpenAgent.csproj` — add ProjectReference
- Modify: `src/agent/OpenAgent/Program.cs` — register keyed singleton + IConfigurable

- [ ] **Step 1: Add project reference to OpenAgent.csproj**

Add to the `<ItemGroup>` containing project references:

```xml
    <ProjectReference Include="..\OpenAgent.LlmText.AnthropicSubscription\OpenAgent.LlmText.AnthropicSubscription.csproj" />
```

- [ ] **Step 2: Add using statement to Program.cs**

Add after the existing `using OpenAgent.LlmText.OpenAIAzure;` line:

```csharp
using OpenAgent.LlmText.AnthropicSubscription;
```

- [ ] **Step 3: Register keyed singleton in Program.cs**

Add after the existing `AddKeyedSingleton<ILlmTextProvider, AzureOpenAiTextProvider>` line:

```csharp
builder.Services.AddKeyedSingleton<ILlmTextProvider, AnthropicSubscriptionTextProvider>(AnthropicSubscriptionTextProvider.ProviderKey);
```

- [ ] **Step 4: Register IConfigurable in Program.cs**

Add after the existing `IConfigurable` registration for the Azure text provider (line ~79):

```csharp
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AnthropicSubscriptionTextProvider.ProviderKey));
```

- [ ] **Step 5: Build the full solution**

```bash
cd src/agent && dotnet build
```
Expected: Build succeeded.

- [ ] **Step 6: Run tests**

```bash
cd src/agent && dotnet test
```
Expected: All existing tests pass (no behavioral changes to existing code).

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent/OpenAgent.csproj src/agent/OpenAgent/Program.cs
git commit -m "feat: register AnthropicSubscription provider in DI"
```

---

## Summary

After all tasks, the provider is available in the settings UI as `anthropic-subscription`. Users configure it with their setup-token and model list, and it can be selected as the text provider for any conversation. The provider handles the full Anthropic Messages API wire format including streaming, tool calls, adaptive thinking, and the OAuth-specific header/system prompt requirements.
