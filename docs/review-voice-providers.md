# Voice Provider Review: Gemini Live & Grok Realtime

Review of the two new `ILlmVoiceProvider` implementations compared against the established Azure OpenAI Realtime provider.

## Overall Assessment

Both implementations are solid and well-structured. They follow the established Azure OpenAI pattern correctly: same constructor shape, DI registration, `IConfigurable` pattern, tool execution flow, and message persistence. The Gemini provider tackles a genuinely different protocol (BidiGenerateContent) with good care. Several issues are worth addressing.

## Issues

### 1. Gemini: No transcript persistence (Medium)

**Files:** `GeminiLiveVoiceSession.cs:372-413`

The Gemini session emits `TranscriptDelta` events for both user and assistant speech but never persists completed transcripts as messages via `AddMessage`. The Grok and Azure providers both persist transcripts when `TranscriptDone` arrives:

```csharp
// Grok/Azure do this — Gemini does not
if (voiceEvent is TranscriptDone td)
{
    var role = td.Source == TranscriptSource.User ? "user" : "assistant";
    _agentLogic.AddMessage(_conversation.Id, new Message { ... });
}
```

Additionally, Gemini never emits `TranscriptDone` — only `TranscriptDelta`. This means user and assistant speech won't appear in conversation history at all.

**Fix:** Emit `TranscriptDone` on `turnComplete`, accumulate transcript deltas to build the full text, and persist via `AddMessage` at that point.

### 2. Gemini: `SingleWriter = true` with multiple potential writers (Medium)

**File:** `GeminiLiveVoiceSession.cs:33`

The channel is created with `SingleWriter = true`, but multiple code paths can write concurrently:

- `HandleServerContentAsync` (from the receive loop)
- `ReconnectAsync` writes `SessionError` at line 236 (from the timer thread pool callback)

The `SingleWriter` hint is advisory and won't throw, but it's misleading and could mask concurrency bugs in future changes.

**Fix:** Change to `SingleWriter = false`.

### 3. Gemini: Null tool call IDs (Low)

**File:** `GeminiMessages.cs:243-245`

`GeminiFunctionCall.Id` is nullable, and the session falls back to `""`. If Gemini doesn't provide IDs, the `ToolCallId = capturedCallId` on persisted messages will be empty. This could cause `BuildChatMessages` orphaned-tool-call validation to misfire.

**Fix:** Generate a synthetic ID (e.g. `Guid.NewGuid().ToString()`) when `call.Id` is null.

### 4. Grok: Tool definition shape may differ from spec (Low)

**File:** `GrokEnvelope.cs:128-141`

`GrokToolDefinition` has `name`, `description`, and `parameters` as top-level fields alongside `type = "function"`. The OpenAI Realtime spec wraps these under a `function` key:

```json
{ "type": "function", "function": { "name": "...", "description": "...", "parameters": {} } }
```

The Azure provider uses the same flat shape and it works, so this may be correct for the Realtime protocol (which differs from the Chat Completions spec). Worth verifying against Grok's actual API docs.

### 5. Grok: High code duplication with Azure provider (Design)

`GrokVoiceSession` is approximately 95% identical to `AzureOpenAiVoiceSession`. The only differences are:

- Endpoint URL (`wss://api.x.ai/v1/realtime` vs Azure's `wss://{host}/openai/realtime`)
- Auth header (`Authorization: Bearer` vs `api-key`)
- Two event type name constants (`response.output_audio.*` vs `response.audio.*`)

Everything else — receive loop, tool execution, transcript persistence, event mapping, dispose — is character-for-character the same. Not urgent, but a future refactor could extract a shared `OpenAiRealtimeSessionBase` class parameterized by endpoint/auth/event-names.

### 6. Gemini: API key in WebSocket URL (Info)

**File:** `GeminiLiveVoiceSession.cs:123-126`

The API key is passed as a query parameter (`?key={apiKey}`). This is Google's required auth mechanism for this API, but the key will appear in server/proxy logs. Not a bug — just a difference from the header-based auth used by the other two providers.

### 7. Both: `JsonSerializerOptions` instantiated per call (Nit)

**Files:** `GrokRealtimeVoiceProvider.cs:36`, `GeminiLiveVoiceProvider.cs:37`

Both `Configure` methods create `new JsonSerializerOptions { PropertyNameCaseInsensitive = true }` inline. These should be `static readonly` to avoid repeated allocation and internal reflection caching.

### 8. Both: Anonymous types for API payloads (Nit)

**File:** `GrokVoiceSession.cs:137`

```csharp
Item = new { type = "function_call_output", call_id = callId, output = result }
```

CLAUDE.md coding conventions say "never anonymous types for API payloads". This pattern also exists in the Azure provider, so it's pre-existing, but should be cleaned up across all three providers.

## Summary Table

| # | Provider | Severity | Issue |
|---|----------|----------|-------|
| 1 | Gemini | Medium | No transcript persistence — speech missing from history |
| 2 | Gemini | Medium | `SingleWriter = true` with concurrent writers |
| 3 | Gemini | Low | Null tool call IDs may break orphan detection |
| 4 | Grok | Low | Tool definition shape — verify against docs |
| 5 | Grok | Design | ~95% code duplication with Azure provider |
| 6 | Gemini | Info | API key in URL (Google's design) |
| 7 | Both | Nit | Static `JsonSerializerOptions` |
| 8 | Both | Nit | Anonymous types for API payloads |
