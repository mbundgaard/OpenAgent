# Global Provider Configuration Design

**Goal:** Introduce a global configuration that maps named slots (text, voice, compaction) to provider+model pairs. Stamp provider+model on conversations at creation. Allow the compaction summarizer to use the text provider instead of making direct HTTP calls.

## Current State

- One `ILlmTextProvider` (AzureOpenAiTextProvider) and one `ILlmVoiceProvider` (AzureOpenAiRealtimeVoiceProvider), each registered as singletons
- Provider key values are generic: `"text-provider"`, `"voice-provider"`
- Endpoints inject the singleton directly — no provider selection
- `CompactionSummarizer` has its own `CompactionLlmConfig` and `HttpClient`, duplicating the Azure OpenAI HTTP logic
- Conversations have no knowledge of which provider or model they use
- `CompleteAsync` always uses the single configured deployment

## Design

### 1. Provider Key Constants

Each provider class gets a `const string ProviderKey` field. The `Key` property forwards to it. Values become implementation-specific:

| Class | ProviderKey |
|-------|-------------|
| `AzureOpenAiTextProvider` | `"azure-openai-text"` |
| `AzureOpenAiRealtimeVoiceProvider` | `"azure-openai-voice"` |

Existing config files on disk (`config/text-provider.json`, `config/voice-provider.json`) must be renamed to match.

### 2. AgentConfig

New `IConfigurable` implementation in the host project with key `"agent"`. Holds three slots and implements the full `IConfigurable` contract (`ConfigFields`, `Configure`):

```csharp
public sealed class AgentConfig : IConfigurable
{
    public const string ConfigKey = "agent";
    public string Key => ConfigKey;

    public string TextProvider { get; set; } = AzureOpenAiTextProvider.ProviderKey;
    public string TextModel { get; set; } = "gpt-5.2-chat";

    public string VoiceProvider { get; set; } = AzureOpenAiRealtimeVoiceProvider.ProviderKey;
    public string VoiceModel { get; set; } = "gpt-realtime";

    public string CompactionProvider { get; set; } = AzureOpenAiTextProvider.ProviderKey;
    public string CompactionModel { get; set; } = "gpt-4.1-mini";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } = [ /* six fields */ ];
    public void Configure(JsonElement configuration) { /* deserialize all six properties */ }
}
```

Managed via the existing admin API at `GET/POST /api/admin/providers/agent/config`. Persisted as `config/agent.json`. Loaded on startup via the existing `IConfigurable` loop in `Program.cs`.

### 3. Conversation Model

Two new required fields on `Conversation`:

```csharp
[JsonPropertyName("provider")]
public required string Provider { get; set; }

[JsonPropertyName("model")]
public required string Model { get; set; }
```

SQLite schema migration adds `Provider TEXT NOT NULL DEFAULT ''` and `Model TEXT NOT NULL DEFAULT ''` columns. No backfill needed — the existing database has been deleted.

The `GetOrCreate` signature gains `provider` and `model` parameters:

```csharp
Conversation GetOrCreate(string conversationId, string source, ConversationType type, string provider, string model);
```

The caller (endpoint or connection handler) resolves the defaults from `AgentConfig` and passes them in. The store stays decoupled from `AgentConfig`. Mapping by `ConversationType`:
- `Text`, `Cron`, `WebHook` -> `AgentConfig.TextProvider` + `AgentConfig.TextModel`
- `Voice` -> `AgentConfig.VoiceProvider` + `AgentConfig.VoiceModel`

### 4. Keyed DI for Provider Resolution

Providers are registered with keyed DI for runtime resolution by conversation provider key:

```csharp
// Keyed registration for resolution
builder.Services.AddKeyedSingleton<ILlmTextProvider, AzureOpenAiTextProvider>(
    AzureOpenAiTextProvider.ProviderKey);

// Unkeyed IConfigurable for admin API enumeration (same instance)
builder.Services.AddSingleton<IConfigurable>(sp =>
    sp.GetRequiredKeyedService<ILlmTextProvider>(AzureOpenAiTextProvider.ProviderKey));
```

Endpoints resolve the provider from the conversation's stamped key:

```csharp
var provider = app.Services.GetRequiredKeyedService<ILlmTextProvider>(conversation.Provider);
```

### 5. ILlmTextProvider: Lower-Level CompleteAsync

Add a second `CompleteAsync` overload for raw completions without a conversation:

```csharp
IAsyncEnumerable<CompletionEvent> CompleteAsync(
    IReadOnlyList<Message> messages,
    string model,
    CompletionOptions? options = null,
    CancellationToken ct = default);
```

`Message` is the existing shared model in `OpenAgent.Models`. `CompletionOptions` is a new simple record in `OpenAgent.Models` for optional settings like response format:

```csharp
public sealed record CompletionOptions
{
    public string? ResponseFormat { get; init; } // e.g. "json_object"
}
```

The existing `CompleteAsync(Conversation, Message)` reads `conversation.Model` instead of `_config.DeploymentName` and delegates to the same HTTP logic internally.

This lower-level method is used by the compaction summarizer (with `ResponseFormat = "json_object"`) and any future non-conversation callers.

### 6. CompactionSummarizer Refactor

`CompactionSummarizer` drops its own `HttpClient` and `CompactionLlmConfig`. Instead:

- Injects a provider factory `Func<string, ILlmTextProvider>` and `AgentConfig` (for compaction slot)
- The factory is registered in DI: `builder.Services.AddSingleton<Func<string, ILlmTextProvider>>(sp => key => sp.GetRequiredKeyedService<ILlmTextProvider>(key));`
- `SummarizeAsync` builds system+user messages, resolves the provider via the factory using `agentConfig.CompactionProvider`, and calls `CompleteAsync(messages, agentConfig.CompactionModel, new CompletionOptions { ResponseFormat = "json_object" })`
- Collects the text deltas and parses the result as JSON

Compaction is always available when a text provider is configured. No separate `CompactionLlmConfig` needed.

### 7. Deletions

- `CompactionLlmConfig.cs` — replaced by `AgentConfig.CompactionProvider` + `CompactionModel`
- Conditional compaction wiring in `Program.cs` — `CompactionSummarizer` is always registered
- Direct HTTP logic in `CompactionSummarizer` — replaced by `ILlmTextProvider` call

## Data Flow

### Conversation creation
```
Client -> Endpoint -> Read AgentConfig defaults for ConversationType
                   -> Store.GetOrCreate(id, source, type, provider, model)
                        |-> Stamps provider+model on Conversation
```

### Text completion
```
Endpoint -> Resolve ILlmTextProvider by conversation.Provider (keyed DI)
         -> provider.CompleteAsync(conversation, userMessage)
             |-> Uses conversation.Model as deployment name
```

### Compaction
```
Store.Update() -> TryStartCompaction()
    -> CompactionSummarizer.SummarizeAsync(messages)
        -> Resolve ILlmTextProvider by AgentConfig.CompactionProvider (keyed DI)
        -> provider.CompleteAsync(messages, AgentConfig.CompactionModel)
```

## Testing

- `InMemoryConversationStore` updated: `GetOrCreate` accepts `provider` and `model` parameters
- Existing tests updated for the new required `Provider`/`Model` fields on `Conversation`
- Compaction tests unchanged — `FakeCompactionSummarizer` already exists
- Lower-level `CompleteAsync(messages, model)` testable via fake text provider
