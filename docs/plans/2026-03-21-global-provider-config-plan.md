# Global Provider Configuration Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce a global `AgentConfig` that maps named slots (text, voice, compaction) to provider+model pairs, stamp provider+model on conversations at creation, and refactor CompactionSummarizer to use the text provider instead of direct HTTP calls.

**Architecture:** `AgentConfig` is a plain data class in `OpenAgent.Models` (shared by all projects), with an `IConfigurable` wrapper in the host for admin API exposure. `GetOrCreate` gains `provider` and `model` parameters — callers resolve defaults from `AgentConfig`. Providers register with keyed DI for resolution by key. `AzureOpenAiTextProvider` gains a lower-level `CompleteAsync(messages, model, options)` overload. `CompactionSummarizer` drops its own HTTP client and uses the text provider via a factory delegate.

**Tech Stack:** .NET 10, ASP.NET Core keyed DI, SQLite, xUnit

**Spec:** `docs/plans/2026-03-21-global-provider-config-design.md`

---

## Chunk 1: Provider Key Constants

### Task 1: Rename provider keys to implementation-specific values

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- Modify: `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiRealtimeVoiceProvider.cs`

- [ ] **Step 1: Add ProviderKey constant to AzureOpenAiTextProvider**

Add above the `Key` property:

```csharp
public const string ProviderKey = "azure-openai-text";
```

Change `Key`:
```csharp
public string Key => ProviderKey;
```

- [ ] **Step 2: Add ProviderKey constant to AzureOpenAiRealtimeVoiceProvider**

Add above the `Key` property:

```csharp
public const string ProviderKey = "azure-openai-voice";
```

Change `Key`:
```csharp
public string Key => ProviderKey;
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Success

- [ ] **Step 4: Commit**

```
feat: rename provider keys to implementation-specific values
```

**Note:** Existing config files on the server (`config/text-provider.json`, `config/voice-provider.json`) must be manually renamed to `config/azure-openai-text.json` and `config/azure-openai-voice.json` after deployment. Without this, providers will be unconfigured on restart and need reconfiguration via the admin API.

---

## Chunk 2: CompletionOptions Model

### Task 2: Add CompletionOptions model

**Files:**
- Create: `src/agent/OpenAgent.Models/Common/CompletionOptions.cs`

- [ ] **Step 1: Create CompletionOptions**

```csharp
using System.Text.Json.Serialization;

namespace OpenAgent.Models.Common;

/// <summary>
/// Optional settings for raw LLM completions (without a conversation context).
/// </summary>
public sealed record CompletionOptions
{
    /// <summary>Response format hint, e.g. "json_object" for structured output.</summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; init; }
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Success

- [ ] **Step 3: Commit**

```
feat: add CompletionOptions model for raw LLM completions
```

---

## Chunk 3: AgentConfig

### Task 3: Create AgentConfig data class and IConfigurable wrapper

**Files:**
- Create: `src/agent/OpenAgent.Models/AgentConfig.cs`
- Create: `src/agent/OpenAgent/AgentConfigConfigurable.cs`

- [ ] **Step 1: Create AgentConfig in OpenAgent.Models**

Plain data class with string literal defaults (no provider project references):

```csharp
using System.Text.Json.Serialization;

namespace OpenAgent.Models;

/// <summary>
/// Global agent configuration — default provider+model for text, voice, and compaction slots.
/// </summary>
public sealed class AgentConfig
{
    public const string ConfigKey = "agent";

    /// <summary>Default text provider key for new conversations.</summary>
    [JsonPropertyName("textProvider")]
    public string TextProvider { get; set; } = "azure-openai-text";

    /// <summary>Default text model/deployment for new conversations.</summary>
    [JsonPropertyName("textModel")]
    public string TextModel { get; set; } = "gpt-5.2-chat";

    /// <summary>Default voice provider key for new conversations.</summary>
    [JsonPropertyName("voiceProvider")]
    public string VoiceProvider { get; set; } = "azure-openai-voice";

    /// <summary>Default voice model/deployment for new conversations.</summary>
    [JsonPropertyName("voiceModel")]
    public string VoiceModel { get; set; } = "gpt-realtime";

    /// <summary>Provider key used for compaction summarization.</summary>
    [JsonPropertyName("compactionProvider")]
    public string CompactionProvider { get; set; } = "azure-openai-text";

    /// <summary>Model/deployment used for compaction summarization.</summary>
    [JsonPropertyName("compactionModel")]
    public string CompactionModel { get; set; } = "gpt-4.1-mini";
}
```

- [ ] **Step 2: Create AgentConfigConfigurable wrapper in host project**

```csharp
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models;
using OpenAgent.Models.Providers;

namespace OpenAgent;

/// <summary>
/// IConfigurable wrapper for AgentConfig — exposes it via the admin API.
/// Lives in the host project because IConfigurable requires ProviderConfigField.
/// </summary>
public sealed class AgentConfigConfigurable(AgentConfig agentConfig) : IConfigurable
{
    public string Key => AgentConfig.ConfigKey;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "textProvider", Label = "Text Provider", Type = "String", Required = true, DefaultValue = "azure-openai-text" },
        new() { Key = "textModel", Label = "Text Model", Type = "String", Required = true, DefaultValue = "gpt-5.2-chat" },
        new() { Key = "voiceProvider", Label = "Voice Provider", Type = "String", Required = true, DefaultValue = "azure-openai-voice" },
        new() { Key = "voiceModel", Label = "Voice Model", Type = "String", Required = true, DefaultValue = "gpt-realtime" },
        new() { Key = "compactionProvider", Label = "Compaction Provider", Type = "String", Required = true, DefaultValue = "azure-openai-text" },
        new() { Key = "compactionModel", Label = "Compaction Model", Type = "String", Required = true, DefaultValue = "gpt-4.1-mini" }
    ];

    public void Configure(JsonElement configuration)
    {
        if (configuration.TryGetProperty("textProvider", out var tp))
            agentConfig.TextProvider = tp.GetString()!;
        if (configuration.TryGetProperty("textModel", out var tm))
            agentConfig.TextModel = tm.GetString()!;
        if (configuration.TryGetProperty("voiceProvider", out var vp))
            agentConfig.VoiceProvider = vp.GetString()!;
        if (configuration.TryGetProperty("voiceModel", out var vm))
            agentConfig.VoiceModel = vm.GetString()!;
        if (configuration.TryGetProperty("compactionProvider", out var cp))
            agentConfig.CompactionProvider = cp.GetString()!;
        if (configuration.TryGetProperty("compactionModel", out var cm))
            agentConfig.CompactionModel = cm.GetString()!;
    }
}
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Success

- [ ] **Step 4: Commit**

```
feat: add AgentConfig data class and IConfigurable wrapper
```

---

## Chunk 4: Conversation Model + Store + All Call Sites (atomic)

This chunk updates the `Conversation` model, `IConversationStore`, both store implementations, and all call sites in a single commit to keep the build green throughout.

### Task 4: Add Provider and Model to Conversation, update stores and all call sites

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Conversation.cs`
- Modify: `src/agent/OpenAgent.Contracts/IConversationStore.cs`
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Modify: `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`
- Modify: `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs`
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebSocketTextEndpoints.cs`
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`
- Modify: `src/agent/OpenAgent.Tests/ConversationEndpointTests.cs`
- Modify: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`
- Modify: `src/agent/OpenAgent.Tests/ExpandToolTests.cs`

- [ ] **Step 1: Add Provider and Model to Conversation model**

In `Conversation.cs`, add after the `Type` property:

```csharp
/// <summary>Provider key used for this conversation (e.g. "azure-openai-text").</summary>
[JsonPropertyName("provider")]
public required string Provider { get; set; }

/// <summary>Model/deployment used for this conversation (e.g. "gpt-5.2-chat").</summary>
[JsonPropertyName("model")]
public required string Model { get; set; }
```

- [ ] **Step 2: Update IConversationStore.GetOrCreate signature**

In `IConversationStore.cs`, change:
```csharp
Conversation GetOrCreate(string conversationId, string source, ConversationType type);
```
To:
```csharp
/// <summary>Returns the existing conversation or creates a new one stamped with provider and model.</summary>
Conversation GetOrCreate(string conversationId, string source, ConversationType type, string provider, string model);
```

- [ ] **Step 3: Update SqliteConversationStore**

In `InitializeDatabase()`, add migrations:
```csharp
TryAddColumn(connection, "Conversations", "Provider", "TEXT NOT NULL DEFAULT ''");
TryAddColumn(connection, "Conversations", "Model", "TEXT NOT NULL DEFAULT ''");
```

Update `GetOrCreate` signature and construction:
```csharp
public Conversation GetOrCreate(string conversationId, string source, ConversationType type, string provider, string model)
```

```csharp
var conversation = new Conversation
{
    Id = conversationId,
    Source = source,
    Type = type,
    Provider = provider,
    Model = model,
    CreatedAt = DateTimeOffset.UtcNow
};
```

Update INSERT SQL to include `Provider, Model` with parameters `@provider, @model`.

Update SELECT queries in `Get()` and `GetAll()` to include `Provider, Model` (indices 10, 11).

Update `ReadConversation` to read the new columns:
```csharp
Provider = reader.GetString(10),
Model = reader.GetString(11)
```

Update `Update()` SQL to include `Provider = @provider, Model = @model` with parameters.

- [ ] **Step 4: Update InMemoryConversationStore**

Update `GetOrCreate` signature:
```csharp
public Conversation GetOrCreate(string conversationId, string source, ConversationType type, string provider, string model)
```

Update construction:
```csharp
var conversation = new Conversation
{
    Id = conversationId,
    Source = source,
    Type = type,
    Provider = provider,
    Model = model
};
```

- [ ] **Step 5: Update all endpoint GetOrCreate calls**

Use hardcoded defaults for now — will be replaced with `AgentConfig` in Task 8.

`ChatEndpoints.cs`:
```csharp
var conversation = store.GetOrCreate(conversationId, "app", ConversationType.Text, "azure-openai-text", "gpt-5.2-chat");
```

`WebSocketTextEndpoints.cs`:
```csharp
var conversation = store.GetOrCreate(conversationId, "app", ConversationType.Text, "azure-openai-text", "gpt-5.2-chat");
```

`WebSocketVoiceEndpoints.cs`:
```csharp
store.GetOrCreate(conversationId, "app", ConversationType.Voice, "azure-openai-voice", "gpt-realtime");
```

`TelegramMessageHandler.cs`:
```csharp
var conversation = _store.GetOrCreate(_conversationId, "telegram", ConversationType.Text, "azure-openai-text", "gpt-5.2-chat");
```

- [ ] **Step 6: Update all test GetOrCreate calls**

All test files: use `"test-provider"` and `"test-model"`.

`ConversationEndpointTests.cs` (both calls):
```csharp
store.GetOrCreate(Guid.NewGuid().ToString(), "app", ConversationType.Text, "test-provider", "test-model");
```

`SqliteConversationStoreTests.cs` (all calls):
```csharp
_store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");
```

`ExpandToolTests.cs`:
```csharp
store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");
```

`TelegramMessageHandlerTests.cs` — the tests construct `InMemoryConversationStore` and call `GetOrCreate` indirectly via `TelegramMessageHandler`. The handler calls `_store.GetOrCreate(...)` internally, which now needs the extra params. Since the handler hardcodes them (Step 5), no test changes needed for `GetOrCreate` calls in tests — but the `InMemoryConversationStore` used in tests needs the updated signature (done in Step 4).

- [ ] **Step 7: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass

- [ ] **Step 8: Commit**

```
feat: add Provider and Model to Conversation, update stores and all call sites
```

---

## Chunk 5: Lower-Level CompleteAsync

### Task 5: Add lower-level CompleteAsync to ILlmTextProvider and implement it

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/ILlmTextProvider.cs`
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- Create: `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ResponseFormatSpec.cs`

- [ ] **Step 1: Add overload to ILlmTextProvider interface**

```csharp
/// <summary>
/// Runs a raw completion without conversation context — no tool calls, no message
/// persistence, no system prompt. Used by compaction and other non-conversation callers.
/// </summary>
IAsyncEnumerable<CompletionEvent> CompleteAsync(
    IReadOnlyList<Message> messages,
    string model,
    CompletionOptions? options = null,
    CancellationToken ct = default);
```

Add usings:
```csharp
using OpenAgent.Models.Common;
```

- [ ] **Step 2: Create ResponseFormatSpec model**

Create `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ResponseFormatSpec.cs`:

```csharp
using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.OpenAIAzure.Models;

public sealed class ResponseFormatSpec
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}
```

Add to `ChatCompletionRequest.cs`:
```csharp
[JsonPropertyName("response_format")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public ResponseFormatSpec? ResponseFormat { get; set; }
```

- [ ] **Step 3: Implement the lower-level CompleteAsync in AzureOpenAiTextProvider**

This is a simple implementation that converts `Message` to `ChatMessage`, builds a non-streaming (or streaming) request with the given model and optional response format, and yields `TextDelta` events. No tool calls, no persistence.

```csharp
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
```

Note: This duplicates some HTTP/SSE logic from the existing `CompleteAsync`. A future refactor could extract shared logic, but for now we keep the two methods independent to minimize risk.

- [ ] **Step 4: Use conversation.Model in existing CompleteAsync**

In the existing `CompleteAsync(Conversation, Message)`, change the URL from using `_config.DeploymentName`:

```csharp
var url = $"openai/deployments/{_config.DeploymentName}/chat/completions?api-version={_config.ApiVersion}";
```

To:
```csharp
var url = $"openai/deployments/{conversation.Model}/chat/completions?api-version={_config.ApiVersion}";
```

The `_config.DeploymentName` field remains in the provider config (for backward compat and as a reference) but is no longer used in the URL — the model always comes from the conversation.

- [ ] **Step 5: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass

- [ ] **Step 6: Commit**

```
feat: add lower-level CompleteAsync and use conversation.Model for deployment
```

---

## Chunk 6: Keyed DI and Program.cs Wiring

### Task 6: Wire keyed DI, AgentConfig, and update Telegram factory

**Files:**
- Modify: `src/agent/OpenAgent/Program.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProviderFactory.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`

- [ ] **Step 1: Register AgentConfig and its IConfigurable wrapper in Program.cs**

Add after `CompactionConfig` registration:

```csharp
var agentConfig = new AgentConfig();
builder.Services.AddSingleton(agentConfig);
builder.Services.AddSingleton<IConfigurable>(new AgentConfigConfigurable(agentConfig));
```

Add using: `using OpenAgent.Models;`

- [ ] **Step 2: Switch to keyed DI for providers**

Replace:
```csharp
builder.Services.AddSingleton<ILlmVoiceProvider, AzureOpenAiRealtimeVoiceProvider>();
builder.Services.AddSingleton<ILlmTextProvider, AzureOpenAiTextProvider>();
```

With:
```csharp
builder.Services.AddKeyedSingleton<ILlmTextProvider, AzureOpenAiTextProvider>(AzureOpenAiTextProvider.ProviderKey);
builder.Services.AddKeyedSingleton<ILlmVoiceProvider, AzureOpenAiRealtimeVoiceProvider>(AzureOpenAiRealtimeVoiceProvider.ProviderKey);

// Provider factory for resolving text providers by key at runtime
builder.Services.AddSingleton<Func<string, ILlmTextProvider>>(sp =>
    key => sp.GetRequiredKeyedService<ILlmTextProvider>(key));
```

- [ ] **Step 3: Update IConfigurable registrations for providers**

Replace:
```csharp
builder.Services.AddSingleton<IConfigurable>(sp => sp.GetRequiredService<ILlmTextProvider>());
builder.Services.AddSingleton<IConfigurable>(sp => sp.GetRequiredService<ILlmVoiceProvider>());
```

With:
```csharp
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey));
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmVoiceProvider>(AzureOpenAiRealtimeVoiceProvider.ProviderKey));
```

- [ ] **Step 4: Remove conditional compaction wiring, register unconditionally**

Delete the entire `var compactionEndpoint = ...` block and add:

```csharp
builder.Services.AddSingleton<ICompactionSummarizer, CompactionSummarizer>();
```

- [ ] **Step 5: Update TelegramChannelProviderFactory registration**

The factory currently injects `ILlmTextProvider` directly (non-keyed). Resolve it via keyed DI in the registration:

Replace:
```csharp
builder.Services.AddSingleton<IChannelProviderFactory, TelegramChannelProviderFactory>();
```

With:
```csharp
builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
{
    var cfg = sp.GetRequiredService<AgentConfig>();
    var textProvider = sp.GetRequiredKeyedService<ILlmTextProvider>(cfg.TextProvider);
    return new TelegramChannelProviderFactory(
        sp.GetRequiredService<IConversationStore>(),
        textProvider,
        cfg.TextProvider,
        cfg.TextModel,
        sp.GetRequiredService<ILoggerFactory>());
});
```

- [ ] **Step 6: Update TelegramChannelProviderFactory to accept provider+model strings**

Add `providerKey` and `model` parameters to the constructor:

```csharp
public sealed class TelegramChannelProviderFactory : IChannelProviderFactory
{
    private readonly IConversationStore _store;
    private readonly ILlmTextProvider _textProvider;
    private readonly string _providerKey;
    private readonly string _model;
    private readonly ILoggerFactory _loggerFactory;

    public string Type => "telegram";

    public TelegramChannelProviderFactory(
        IConversationStore store,
        ILlmTextProvider textProvider,
        string providerKey,
        string model,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _textProvider = textProvider;
        _providerKey = providerKey;
        _model = model;
        _loggerFactory = loggerFactory;
    }

    public IChannelProvider Create(Connection connection)
    {
        var options = JsonSerializer.Deserialize<TelegramOptions>(connection.Config,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialize Telegram config for connection '{connection.Id}'.");

        return new TelegramChannelProvider(
            options,
            connection.ConversationId,
            _store,
            _textProvider,
            _providerKey,
            _model,
            _loggerFactory.CreateLogger<TelegramChannelProvider>(),
            _loggerFactory.CreateLogger<TelegramMessageHandler>());
    }
}
```

- [ ] **Step 7: Pass provider+model through TelegramChannelProvider to TelegramMessageHandler**

Update `TelegramChannelProvider` constructor to accept `string providerKey, string model` and pass them through to `TelegramMessageHandler`.

Update `TelegramMessageHandler` constructor to accept `string provider, string model`, store as fields `_provider` and `_model`, and use in `GetOrCreate`:

```csharp
var conversation = _store.GetOrCreate(_conversationId, "telegram", ConversationType.Text, _provider, _model);
```

- [ ] **Step 8: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass

- [ ] **Step 9: Commit**

```
feat: wire keyed DI, AgentConfig, and update Telegram factory
```

---

## Chunk 7: Endpoints Use AgentConfig

### Task 7: Update endpoints to use AgentConfig for GetOrCreate and provider resolution

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs`
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebSocketTextEndpoints.cs`
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs`

- [ ] **Step 1: Update ChatEndpoints**

Inject `AgentConfig` (from `OpenAgent.Models`, which `OpenAgent.Api` already references) and resolve the keyed text provider:

```csharp
app.MapPost("/api/conversations/{conversationId}/messages", async (
    string conversationId,
    ChatRequest request,
    IConversationStore store,
    AgentConfig agentConfig,
    IServiceProvider services,
    CancellationToken ct) =>
{
    var conversation = store.GetOrCreate(conversationId, "app", ConversationType.Text,
        agentConfig.TextProvider, agentConfig.TextModel);

    var textProvider = services.GetRequiredKeyedService<ILlmTextProvider>(conversation.Provider);

    var userMessage = new Message
    {
        Id = Guid.NewGuid().ToString(),
        ConversationId = conversationId,
        Role = "user",
        Content = request.Content
    };

    var events = new List<object>();
    await foreach (var evt in textProvider.CompleteAsync(conversation, userMessage, ct))
    {
        // ... existing event handling unchanged
    }

    return Results.Json(events, JsonOptions);
}).RequireAuthorization();
```

Add using: `using OpenAgent.Models;`

Also add using for keyed DI: `using Microsoft.Extensions.DependencyInjection;`

- [ ] **Step 2: Update WebSocketTextEndpoints**

```csharp
app.Map("/ws/conversations/{conversationId}/text", async (string conversationId, HttpContext context,
    IConversationStore store, AgentConfig agentConfig, IServiceProvider services) =>
{
    // ...
    var conversation = store.GetOrCreate(conversationId, "app", ConversationType.Text,
        agentConfig.TextProvider, agentConfig.TextModel);

    var textProvider = services.GetRequiredKeyedService<ILlmTextProvider>(conversation.Provider);
    // ... pass textProvider to RunChatLoopAsync instead of the injected one
```

Update `RunChatLoopAsync` to accept `ILlmTextProvider` as a parameter instead of capturing a field.

- [ ] **Step 3: Update WebSocketVoiceEndpoints**

```csharp
app.Map("/ws/conversations/{conversationId}/voice", async (string conversationId, HttpContext context,
    IConversationStore store, AgentConfig agentConfig, IVoiceSessionManager sessionManager) =>
{
    // ...
    store.GetOrCreate(conversationId, "app", ConversationType.Voice,
        agentConfig.VoiceProvider, agentConfig.VoiceModel);
```

Add using: `using OpenAgent.Models;`

- [ ] **Step 4: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass

- [ ] **Step 5: Commit**

```
feat: endpoints use AgentConfig for provider+model defaults
```

---

## Chunk 8: Refactor CompactionSummarizer

### Task 8: Refactor CompactionSummarizer to use ILlmTextProvider

**Files:**
- Modify: `src/agent/OpenAgent.Compaction/CompactionSummarizer.cs`
- Delete: `src/agent/OpenAgent.Compaction/CompactionLlmConfig.cs`

- [ ] **Step 1: Rewrite CompactionSummarizer**

Replace the class. Uses a provider factory delegate and `AgentConfig` instead of direct HTTP:

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Calls the LLM via an ILlmTextProvider to generate a structured compaction summary.
/// </summary>
public sealed class CompactionSummarizer : ICompactionSummarizer
{
    private readonly Func<string, ILlmTextProvider> _providerFactory;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<CompactionSummarizer> _logger;

    public CompactionSummarizer(
        Func<string, ILlmTextProvider> providerFactory,
        AgentConfig agentConfig,
        ILogger<CompactionSummarizer> logger)
    {
        _providerFactory = providerFactory;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public async Task<CompactionResult> SummarizeAsync(string? existingContext, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        var userContent = new StringBuilder();

        if (existingContext is not null)
        {
            userContent.AppendLine("## Existing Context (from previous compaction)");
            userContent.AppendLine(existingContext);
            userContent.AppendLine();
        }

        userContent.AppendLine("## Messages to Compact");
        foreach (var msg in messages)
        {
            userContent.AppendLine($"[{msg.Id}] [{msg.CreatedAt:yyyy-MM-dd HH:mm}] [{msg.Role}]: {msg.Content}");
            if (msg.ToolCalls is not null)
                userContent.AppendLine($"  Tool calls: {msg.ToolCalls}");
            if (msg.ToolCallId is not null)
                userContent.AppendLine($"  (tool result for call {msg.ToolCallId})");
        }

        var llmMessages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = CompactionPrompt.System },
            new() { Id = "usr", ConversationId = "", Role = "user", Content = userContent.ToString() }
        };

        var provider = _providerFactory(_agentConfig.CompactionProvider);
        var options = new CompletionOptions { ResponseFormat = "json_object" };

        var fullContent = new StringBuilder();
        await foreach (var evt in provider.CompleteAsync(llmMessages, _agentConfig.CompactionModel, options, ct))
        {
            if (evt is TextDelta delta)
                fullContent.Append(delta.Content);
        }

        var content = fullContent.ToString();
        using var doc = JsonDocument.Parse(content);
        var context = doc.RootElement.GetProperty("context").GetString()!;
        var memories = doc.RootElement.TryGetProperty("memories", out var mem)
            ? mem.EnumerateArray().Select(m => m.GetString()!).ToList()
            : new List<string>();

        _logger.LogInformation("Compaction summary generated: {Length} chars, {MemoryCount} memories", context.Length, memories.Count);

        return new CompactionResult { Context = context, Memories = memories };
    }
}
```

- [ ] **Step 2: Delete CompactionLlmConfig.cs**

Delete `src/agent/OpenAgent.Compaction/CompactionLlmConfig.cs`.

- [ ] **Step 3: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass

- [ ] **Step 4: Commit**

```
feat: refactor CompactionSummarizer to use ILlmTextProvider
```
