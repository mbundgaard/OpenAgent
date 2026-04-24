# LLM Providers Review — 2026-04-23

## TL;DR
Two hand-rolled text providers (Azure OpenAI, Anthropic setup-token) and three WebSocket voice providers (Azure Realtime, Grok, Gemini Live). Streaming, tool loops and persistence are structurally sound and the Anthropic OAuth mechanics (per-request `Authorization`, Claude Code identity prefix, adaptive thinking gate, `anthropic-beta`) match the documented setup-token contract. The problems concentrate on edges: `AzureOpenAiTextProvider` ships without `HttpClient.Timeout` so a stalled upstream hangs forever, `Configure()` disposes `_httpClient` mid-request on both text providers (race on keyed-singleton reconfigure), `AzureOpenAiVoiceSession` fires tool tasks with `Task.Run` untracked so dispose races with live `SendAsync` calls on a disposed socket, `GeminiLiveVoiceSession` swaps `_ws` inside `ReconnectAsync` without `_sendLock` and never replays history on reconnect (15-min session = 15-min memory), `Grok` sets no input transcription so user voice turns never land in conversation history, and both text providers deserialize SSE chunks without `try/catch` so a single malformed frame aborts the turn. Tool-result size is uncapped in the in-memory prompt — a runaway shell_exec can blow the 200K context window in two rounds.

## Strengths
- Per-request `Authorization: Bearer <setup-token>` on Anthropic — matches the documented requirement (`AnthropicSubscriptionTextProvider.cs:124`, `:364`). Identity headers and beta flags are in the expected order (`:70-75`).
- Adaptive thinking gated on model name containing `4-6` — correct for `claude-sonnet-4-6` / `claude-opus-4-6` (`AnthropicSubscriptionTextProvider.cs:102`, `:349`).
- Two-block system prompt array with Claude Code identity prefix (`AnthropicSubscriptionTextProvider.cs:404-415`) — required for OAuth tokens per docs.
- `HttpCompletionOption.ResponseHeadersRead` on both text providers' streaming calls (`AzureOpenAiTextProvider.cs:97, 298`; `AnthropicSubscriptionTextProvider.cs:126, 366`) — correct SSE streaming posture.
- Tool-call loop capped at 10 rounds on both text providers with `InvalidOperationException` on exceed; logs the cap (`AzureOpenAiTextProvider.cs:89, 261-263`; `AnthropicSubscriptionTextProvider.cs:105, 324-326`).
- `BuildChatMessages` / `BuildMessages` actively skip orphaned tool-call rounds with a `WARN` log, preventing API 400 on crashed/compacted histories (`AzureOpenAiTextProvider.cs:358-366`; `AnthropicSubscriptionTextProvider.cs:452-460`).
- Anthropic uses `fine-grained-tool-streaming-2025-05-14` beta + `input_json_delta` accumulation — correct wire handling for partial tool JSON (`AnthropicSubscriptionTextProvider.cs:71, 197-201`).
- Grok voice session tracks fire-and-forget tool tasks via `ConcurrentDictionary<Task, byte>` and awaits them in `DisposeAsync` (`GrokVoiceSession.cs:33, 94-99, 304`) — the pattern Azure voice should copy.
- Gemini `goAway` triggers proactive reconnect before the 15-min server cap (`GeminiLiveVoiceSession.cs:347-352`).
- Conversation is re-fetched after the final round before token accounting, so tool-driven mutations (e.g. skill activation) are not overwritten (`AzureOpenAiTextProvider.cs:247`; `AnthropicSubscriptionTextProvider.cs:310`).
- `ToolResultSummary.Create` stores shape/size only; full result stays in-memory for the *current* tool round only (`AzureOpenAiTextProvider.cs:216-220`; `AnthropicSubscriptionTextProvider.cs:275-278`).
- CompactionSummarizer stays provider-agnostic via the raw `CompleteAsync(messages, model, options)` overload and requests `response_format: json_object` (`CompactionSummarizer.cs:58-66`).
- Voice sessions use `SemaphoreSlim` for send serialization and `Channel<VoiceEvent>` for the producer/consumer boundary — correct pattern (`AzureOpenAiVoiceSession.cs:27-30`; `GrokVoiceSession.cs:29-33`; `GeminiLiveVoiceSession.cs:33-36`).

## Bugs

### `_httpClient` disposed under live request during re-`Configure()` (severity: high)
- **Location:** `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:50-54`, `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:62-67`
- **Issue:** Both text providers are registered as keyed singletons. `Configure()` does `_httpClient?.Dispose(); _httpClient = new HttpClient(...)`. When the admin saves settings while a completion is in flight on another request thread, the in-flight `SendAsync` / `ReadLineAsync` hits `ObjectDisposedException`. The new `HttpClient` is visible to subsequent calls, but the one still streaming for the earlier request dies.
- **Risk:** Noisy errors on any settings save during a live chat; user sees an aborted stream with no recovery. The voice provider is immune (session opens its own `ClientWebSocket`).
- **Fix:** Use `IHttpClientFactory` (preferred) or hold the current client behind a reference-counted handle. Minimal fix: a `ReaderWriterLockSlim` around `Configure` (write) and `CompleteAsync`'s client read (reader). Alternative: make `Configure` return a new provider instance instead of mutating.

### Azure text HttpClient has no `Timeout` — stalled upstream hangs the request forever (severity: high)
- **Location:** `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:51-55`
- **Issue:** No `Timeout` set. Default is 100s, and with `HttpCompletionOption.ResponseHeadersRead` the 100s covers only the header phase — once headers come back, body reads aren't bounded. `reader.ReadLineAsync(ct)` at `:118` blocks until the caller cancels. Anthropic provider correctly sets `TimeSpan.FromMinutes(5)` at `AnthropicSubscriptionTextProvider.cs:66`.
- **Risk:** A single unresponsive stream pins a thread and blocks that conversation's tool execution. On a 1-vCPU B1 Azure App Service this compounds fast.
- **Fix:** Either set `Timeout = Timeout.InfiniteTimeSpan` and rely on `ct` (recommended, since every call receives a CT), or add an inactivity-watchdog: `using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct); readCts.CancelAfter(TimeSpan.FromSeconds(60))`, reset on each line.

### Gemini voice: `_ws` swap in `ReconnectAsync` races `SendMessageAsync` without `_sendLock` (severity: high)
- **Location:** `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs:221-232, 260-266`
- **Issue:** `ReconnectAsync` does `await CloseWebSocketAsync(_ws); _ws.Dispose(); _ws = new ClientWebSocket();` at `:228-230` **without acquiring `_sendLock`**. Concurrent `SendMessageAsync` reads `_ws` inside `_sendLock`, so a message can enter the critical section with the old (disposed) `_ws` reference. `SendAudioAsync` has a `Volatile.Read(ref _reconnecting) == 1` guard at `:71`, but `SendToolResponseAsync` and `CancelResponseAsync` don't.
- **Risk:** `ObjectDisposedException` during reconnect windows. Gemini reconnects every 13 min by design, so this is a regular occurrence.
- **Fix:** Take `_sendLock.WaitAsync()` around the socket swap in `ReconnectAsync`, or make `_ws` volatile and retry on `ObjectDisposedException` in `SendMessageAsync`.

### Gemini voice: reconnect loses all conversation history (severity: high)
- **Location:** `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs:156-203, 212-247`
- **Issue:** `SendSetupAsync` only sends model/voice/tools/system-instruction on connect and on reconnect — never replays prior user/assistant turns from `_agentLogic.GetMessages(conversationId)`. After the 13-minute proactive reconnect or a `goAway`, the new session starts with zero memory. Meanwhile the `_assistantTranscript` / `_userTranscript` accumulators are in-memory only — they don't survive the reconnect because nothing feeds them back into the new session's context.
- **Risk:** "15-minute voice cap handled via proactive reconnect" contract is broken for any real conversation. User speaks for 15 min, system reconnects, user asks "what did I just say?" — agent has no clue.
- **Fix:** Before marking setup complete on reconnect, walk `_agentLogic.GetMessages(_conversation.Id)` and send a `clientContent` turn per stored user/assistant message. Or include them in the `setup` payload's initial turns. Bonus: handle tool call history too.

### Azure voice session: fire-and-forget tool tasks outlive dispose (severity: high)
- **Location:** `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs:262-300` (vs fixed in Grok at `GrokVoiceSession.cs:262-304`)
- **Issue:** On `FunctionCallArgumentsDone`, the handler starts `_ = Task.Run(async () => { ... await SendToolResultAndContinueAsync(...); }, ct)` but never tracks the task. `DisposeAsync` awaits `_receiveTask` only, then disposes `_ws`, `_sendLock`, `_receiveCts`. An in-flight tool (e.g. a 30s shell_exec) continues running; when it calls `SendToolResultAndContinueAsync` → `SendEventAsync` → `_sendLock.WaitAsync(ct)` or `_ws.SendAsync(...)`, it hits `ObjectDisposedException`. The catch block then tries `SendToolResultAndContinueAsync(callId, errorResult, ct)` **again** and hits the same exception — this second one is unobserved, surfacing via `TaskScheduler.UnobservedTaskException`.
- **Risk:** Log noise, half-persisted conversation state (`AddMessage` at `:288` may complete before the socket send fails, leaving an orphan assistant tool-call + error-shaped tool result), and unobserved exception leaks.
- **Fix:** Port the Grok pattern verbatim: `ConcurrentDictionary<Task, byte> _toolTasks`, register before starting, `TryRemove` in finally, `await Task.WhenAll(_toolTasks.Keys.ToArray())` in `DisposeAsync` before disposing primitives. Same fix needed for `GeminiLiveVoiceSession.cs:482` (uncaught `Task.Run` for Gemini tool calls).

### Grok voice: no `InputAudioTranscription` → user speech never persisted (severity: high)
- **Location:** `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokVoiceSession.cs:120-159`
- **Issue:** `SendSessionUpdateAsync` omits input-audio-transcription config. User utterances are understood by the model but never surface as `InputAudioTranscriptionCompleted` events. The handler at `:336` (which calls `AddMessage` with role=user) never fires. Conversation history stores only the assistant side — the user's turns disappear from the DB.
- **Risk:** Per-turn history is one-sided. Next text turn (or any REST inspection of the conversation) shows a half-deaf history. Also breaks `BuildChatMessages` orphan-detection assumptions since there's no user message to pair with the assistant's response.
- **Fix:** Add `InputAudioTranscription = new GrokTranscriptionConfig { Model = "whisper-1" }` to `GrokSessionConfig` in `SendSessionUpdateAsync`. Verify with Grok's actual API docs which model string it expects.

### SSE parsing has no `JsonException` guard — one malformed chunk aborts the whole turn (severity: high)
- **Location:** `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:125, 316`; `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:166, 175, 189, 207, 393`
- **Issue:** Both providers call `JsonSerializer.Deserialize<...>(data)` inside the stream loop with no try/catch. Any malformed frame (keep-alive `\r\n`, a proxy-injected comment line, a content-filter error object with an unexpected shape) throws `JsonException` and aborts the iterator. The user sees a 500 with no partial progress; already-persisted tool messages remain orphaned.
- **Risk:** Azure OpenAI can stream `{"error": {...}}` chunks for content filtering — the current code silently yields an empty response (Choices null → `choice is null; continue;`), or in the worst case the deserialize throws on an unfamiliar shape.
- **Fix:** Wrap deserialize in `try/catch (JsonException ex)` inside the loop: log WARN and `continue;`. Additionally, add an `Error` field to `ChatCompletionResponse` and handle content-filter / safety responses explicitly with a descriptive exception.

### `BuildChatMessages` uses `HashSet<string>` iteration to size a stored-order tool-result copy (severity: high)
- **Location:** `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:372-381`, `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:476-485`
- **Issue:** After `SetEquals(expectedIds, foundIds)` passes, the code does `foreach (var id in expectedIds) { i++; chatMessages.Add(new ChatMessage { ..., ToolCallId = storedMessages[i].ToolCallId }); }`. The loop variable `id` is **unused** — `ToolCallId` and `Content` come from `storedMessages[i]`, not from `id`. Today that's correct (stored order drives the output), but:
  1. Unused `id` is a footgun. A future refactor changing the body to `ToolCallId = id` would silently ship the wrong id paired with the wrong content.
  2. `expectedIds.Count` loop-count depends on the HashSet, which deduplicates. If `toolCalls` ever had a duplicated id (model hallucination), `expectedIds.Count < toolCalls.Count` and the loop under-reads stored messages — leaving the rest of the iteration out of sync.
- **Risk:** Latent; breaks on the first duplicated tool id, or on a well-meaning refactor.
- **Fix:** Iterate the tool messages directly: `for (var k = 0; k < toolCalls.Count; k++) { i++; var toolMsg = storedMessages[i]; chatMessages.Add(new ChatMessage { Role = "tool", Content = toolMsg.Content, ToolCallId = toolMsg.ToolCallId }); }`. Apply identically on the Anthropic mirror.

### Tool result content is sent to the LLM with no size cap (severity: high)
- **Location:** `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:220-225`, `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:280-285`
- **Issue:** `request.Messages.Add(new ChatMessage { Role = "tool", Content = result, ... })` and `toolResultBlocks.Add(new AnthropicContentBlock { Type = "tool_result", ToolUseId = id, Content = result })` ship the raw, potentially multi-MB `result` string. A runaway `shell_exec` or `file_read` can pump GBs into a single tool result. Azure OpenAI default `max_tokens` + 200K Anthropic context = easy to blow with 2-3 tool rounds.
- **Risk:** Users see 400 "prompt too long"; costly requests before the reject (Azure bills input tokens even on 400 for large enough prompts).
- **Fix:** Cap in-memory tool results at a configurable size (e.g. 64 KB). Truncate with a clear marker: `result[..cap] + "\n\n[truncated: N bytes omitted]"`. `ToolResultSummary.Create` is already safe because it stores shape only — only the in-memory `result` plumbing into the LLM needs the cap.

### Voice sessions: no `ClientWebSocket.Options.KeepAliveInterval` configured (severity: medium)
- **Location:** `AzureOpenAiVoiceSession.cs:26, 43-59`; `GrokVoiceSession.cs:28, 46-59`; `GeminiLiveVoiceSession.cs:39, 126-154`
- **Issue:** Default WebSocket keepalive on .NET can be 30s but depends on version/platform; on cloud NAT/LB paths (Azure Front Door, AWS ALB) idle timeouts are 60-240s. Long-quiet voice sessions (agent listening, no speech) silently drop. Next `SendAudioAsync` fails with `WebSocketException`.
- **Risk:** Phantom session deaths during long quiet periods.
- **Fix:** Set `_ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20)` (and on .NET 8+ `_ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10)`) before `ConnectAsync` in each voice provider.

### Voice sessions: `VoiceSessionOpen` not cleared on server-initiated close (severity: medium)
- **Location:** `AzureOpenAiVoiceSession.cs:213-214, 106-111`; `GrokVoiceSession.cs:218, 107-112`
- **Issue:** `ReceiveLoopAsync` returns on `WebSocketMessageType.Close`; `finally` only completes the channel writer. `_conversation.VoiceSessionOpen = false` is only written in `DisposeAsync`. If the server closes and the caller doesn't dispose (frontend crash, WebSocketTextEndpoint unwinding imperfectly), the conversation row stays `VoiceSessionOpen=true` forever, confusing any UI that uses that flag to render a connect button.
- **Risk:** Stuck "voice in progress" UI state; manual DB cleanup needed.
- **Fix:** Clear `VoiceSessionOpen` in the receive loop `finally` block, not only `DisposeAsync`. Also update the conversation on any abnormal close.

### Anthropic: `JsonSerializer.Deserialize<JsonElement>(args)` throws from inside the tool-call assembly path (severity: medium)
- **Location:** `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:251-253, 468-470`
- **Issue:** For every tool call, accumulated arguments are parsed via `JsonSerializer.Deserialize<JsonElement>(tc.Function.Arguments)`. If the model hallucinated non-JSON (possible on very tight token budgets, truncated streams, or early `message_stop`), this throws `JsonException` *after* `AddMessage` has already persisted the raw (invalid) arguments. The turn aborts with a half-written conversation.
- **Risk:** Conversation is stuck — the orphan detector will skip the broken round on the next request, but the user-facing error is a generic 500.
- **Fix:** Wrap in `try/catch (JsonException)`: fall back to `JsonSerializer.Deserialize<JsonElement>("{}")` (cached once at class init), log WARN, continue. Optionally emit an assistant text block: "I tried to call {tool} but the arguments were malformed."

### Voice tool persistence uses anonymous objects, breaking symmetry with text persistence (severity: medium)
- **Location:** `AzureOpenAiVoiceSession.cs:249-252`; `GrokVoiceSession.cs:249-252`; `GeminiLiveVoiceSession.cs:465-468`
- **Issue:** Voice sessions persist tool calls as `JsonSerializer.Serialize(new[] { new { id, type, function = new { name, arguments } } })`. The text providers later deserialize via `List<ToolCall>` (Azure, `AzureOpenAiTextProvider.cs:341`) or `List<StoredToolCall>` (Anthropic, `AnthropicSubscriptionTextProvider.cs:437`). Anthropic enables `PropertyNameCaseInsensitive`; Azure does not (`JsonSerializer.Deserialize<List<ToolCall>>(msg.ToolCalls)` with no options). It works today only because the anonymous property names (`id`, `type`, `function`, `name`, `arguments`) happen to match the `JsonPropertyName` attributes at case-sensitive level. Any future rename (e.g. `Name` → `tool_name`) silently breaks.
- **Risk:** Schema drift lands silently. Persisted-data contract is implicit rather than typed.
- **Fix:** Define `StoredToolCall` / `StoredToolCallFunction` in `OpenAgent.Models/Conversations/` and have all five providers use it for both serialization and deserialization. CLAUDE.md already says "never anonymous types for API payloads" — persisted JSON is an API.

### `AssistantMessageSaved` not handled by REST/WebSocket text endpoints (severity: medium)
- **Location:** `OpenAgent.Api/Endpoints/ChatEndpoints.cs` (REST), `OpenAgent.Api/Endpoints/WebSocketTextEndpoints.cs` (WS) — referenced by `CompletionEvent.cs:28`
- **Issue:** Text providers emit `AssistantMessageSaved(messageId)` as the final completion event (`AzureOpenAiTextProvider.cs:257`, `AnthropicSubscriptionTextProvider.cs:320`). Channel message handlers consume it to correlate outbound channel message IDs with internal message IDs. The REST endpoint and WebSocket text endpoint don't, so the client receives it as `unknown` or not at all, losing the ability to correlate for later state reconciliation (e.g. reply-to-message UI).
- **Risk:** Frontend can't link server-side message IDs to its optimistic drafts.
- **Fix:** Add an explicit case in both endpoints that emits `{ type: "assistant_saved", message_id }` to the client, or silently swallow if not needed at all.

### Azure voice session hardcodes `"whisper-1"` (severity: medium)
- **Location:** `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs:139`
- **Issue:** `InputAudioTranscription = new InputAudioTranscriptionConfig { Model = "whisper-1" }`. Azure Realtime now supports `gpt-4o-transcribe` / `gpt-4o-mini-transcribe` with better accuracy. No way for admins to pick a different model without a rebuild.
- **Risk:** Worse accuracy, no upgrade path.
- **Fix:** Add `transcriptionModel` to `AzureRealtimeConfig` with default `"gpt-4o-mini-transcribe"`, surface it in `ConfigFields`.

### Anthropic user-agent and beta constants will drift (severity: medium)
- **Location:** `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:71-73`
- **Issue:** `anthropic-beta: claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14` and `user-agent: claude-cli/2.1.91` are hardcoded. The docs specifically warn these must track real Claude Code versions; Anthropic has enforced minimum-version in the past. When they rotate, this provider 429s with no path-of-least-resistance recovery unless admins know to bump constants.
- **Risk:** Outage when Anthropic rotates the allowed CLI-emulation set.
- **Fix:** Move to `AnthropicConfig` (or a static readonly with a clear TODO at the top of the file referencing `docs/anthropic-setup-token-auth.md`). Alternatively check at service start whether `claude --version` is available and read from it.

### Setup-token has no expiry / refresh path — 401 surfaces as generic 500 (severity: medium)
- **Location:** `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:128-134`
- **Issue:** Per docs (`docs/anthropic-setup-token-auth.md:21`): "No auto-refresh. Static bearer token." The provider has no detection for 401 responses; they throw `HttpRequestException` caught nowhere specific and surface as 500. Concurrent Claude Code sessions cause 429; those also throw.
- **Risk:** Admin must read logs to discover they need to re-run `claude setup-token`. No proactive monitoring hook.
- **Fix:** Map 401/403 to a typed `AnthropicAuthException` with a message pointing to `claude setup-token`. Add a health-check endpoint that pings Anthropic with `max_tokens: 1`. On 429, retry with jitter once before giving up.

### Voice receive loop swallows `JsonException` during envelope deserialization (severity: medium)
- **Location:** `AzureOpenAiVoiceSession.cs:220-222`; `GrokVoiceSession.cs:223-224`; `GeminiLiveVoiceSession.cs:314-315`
- **Issue:** `JsonSerializer.Deserialize<...>(buffer.WrittenSpan)` — unhandled `JsonException` escapes the `try`; only `OperationCanceledException` and `WebSocketException` are caught. A single malformed frame kills the entire session with no user-visible signal (the `Channel` completes silently).
- **Risk:** Silent session death.
- **Fix:** Add `catch (JsonException ex) { _logger.LogWarning(ex, "bad frame"); continue; }` inside the `while` loop.

### Non-conversation Anthropic overload squashes `tool` role to `user` (severity: medium)
- **Location:** `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:340-343`
- **Issue:** `.Select(m => new AnthropicMessage { Role = m.Role == "tool" ? "user" : m.Role, Content = m.Content ?? "" })`. Compaction only sends system + user, so it works today. Any future caller passing tool messages will have `tool_use_id` linkage erased — Anthropic responds 400 ("tool_use without tool_result").
- **Risk:** Latent. Any future client of this overload that includes tool history silently breaks.
- **Fix:** Either `throw ArgumentException` if messages contain tool role / tool calls, or build proper `tool_use` / `tool_result` blocks.

### Azure text usage chunk may be skipped by `[DONE]` break (severity: low)
- **Location:** `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:123, 127-132`
- **Issue:** The loop `break`s on `[DONE]` before any trailing chunk. OpenAI sends the usage chunk before `[DONE]` today with `stream_options.include_usage: true`, but Azure has historically drifted. If a usage chunk lands after `[DONE]`, the `if (chunk?.Usage is not null)` block never runs — token accounting under-counts.
- **Risk:** Silent under-reporting; the displayed `LastPromptTokens` on the conversation can lag.
- **Fix:** Continue reading after `[DONE]` until EOF, or simply reorder to process the chunk first, then check `data == "[DONE]"`.

### `SessionReady` emitted before server-side `session.created` arrives (severity: low)
- **Location:** `AzureOpenAiVoiceSession.cs:152-158`; `GrokVoiceSession.cs:154-158`
- **Issue:** `SendSessionUpdateAsync` writes `SessionReady` to the channel immediately after `session.update` is sent — but the server's `session.created` (which sets `SessionId`) is handled asynchronously. A consumer receiving `SessionReady` may observe `session.SessionId == ""`.
- **Risk:** Minor; frontend logs an empty session id.
- **Fix:** Wait for `session.created` before emitting `SessionReady`, or include `SessionId` in the emitted event once known.

### `VoiceSessionManager.GetOrCreateSessionAsync` races duplicate session creation (severity: low)
- **Location:** `src/agent/OpenAgent/VoiceSessionManager.cs:22-45`
- **Issue:** `TryGetValue` + `StartSessionAsync` + `TryAdd` — two concurrent calls both open a WebSocket, one wins `TryAdd`, the other disposes. The loser has already issued a `session.update` to the server and burned quota. `StartSessionAsync` involves a network round-trip; the window is not trivial.
- **Risk:** Low frequency, but doubled quota on contention.
- **Fix:** Per-conversation `SemaphoreSlim` gate before `TryGetValue`.

### `conversation.Provider` not validated against voice-provider key set (severity: low)
- **Location:** `src/agent/OpenAgent/VoiceSessionManager.cs:31-35`
- **Issue:** `var providerKey = string.IsNullOrEmpty(conversation.Provider) ? _agentConfig.VoiceProvider : conversation.Provider;` then `_providerFactory(providerKey)`. If `conversation.Provider` was set to a text provider key ("azure-openai-text") and the conversation type was retroactively flipped to voice, the factory throws `KeyNotFoundException`.
- **Risk:** Confusing error on weird legacy state. Conversation-level provider should only apply when meaningful for the conversation type.
- **Fix:** Always use `_agentConfig.VoiceProvider` for voice sessions, or verify the resolved provider implements `ILlmVoiceProvider` before returning.

### Tool argument accumulator is an unbounded `StringBuilder` (severity: low)
- **Location:** `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:110, 160`; `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:138, 197-201`
- **Issue:** No cap. A pathological model streaming gigabytes of tool args before `finish_reason=tool_calls` OOMs the host. Unlikely in practice (both APIs enforce `max_tokens`), but worth a sanity cap.
- **Risk:** Low.
- **Fix:** Reject any single tool call where `args.Length > 1_000_000` with a descriptive error.

### Debug log line emits full message content at 200-char truncation (severity: low)
- **Location:** `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:397-399`; `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:495-497`
- **Issue:** `logger.LogDebug("LLM context [{Role}] {Name}: {Content}", ...)`. 200 chars of user content to Serilog on Debug — fine when log level is Info, but any accidental enable of Debug in prod puts PII in `{dataPath}/logs/log-*.jsonl`.
- **Risk:** Compliance concern for privacy-sensitive deployments.
- **Fix:** Gate behind an explicit `LogPromptContent` flag instead of relying on log level.

### Gemini text parts in `modelTurn` are dropped (severity: low)
- **Location:** `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs:380-390`
- **Issue:** The loop checks `part.InlineData` (audio) but `GeminiPart.Text` is also a field at `GeminiMessages.cs:215` and is ignored. Unlikely with `response_modalities: ["AUDIO"]` only, but not guaranteed.
- **Risk:** Trivial data loss on edge cases.
- **Fix:** Log, or append to `_assistantTranscript`.

### `Gemini.HandleToolCallAsync` has dead `captured*` aliases (severity: low)
- **Location:** `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs:478-480`
- **Issue:** `var capturedName = name; var capturedCallId = callId; var capturedArguments = arguments;` — a pre-C# 5 foreach-closure workaround. Modern C# captures foreach vars correctly. Dead ceremony.
- **Risk:** None; readability only.
- **Fix:** Drop the aliases, close over `name`, `callId`, `arguments` directly.

### Compaction summarizer parses JSON without fallback (severity: low)
- **Location:** `src/agent/OpenAgent.Compaction/CompactionSummarizer.cs:68-75`
- **Issue:** `JsonDocument.Parse(content)` + `GetProperty("context").GetString()!`. Any malformed LLM response or missing field throws and the compaction run aborts with no checkpoint. The `!` forgives null on `GetString()` for non-string kinds — NRE if the LLM emits `"context": 42`.
- **Risk:** Compaction fails and never recovers; next trigger re-attempts from the same cutoff.
- **Fix:** Wrap in `try/catch (JsonException or KeyNotFoundException or InvalidOperationException)`, log WARN, return an empty `CompactionResult` so the cutoff advances. Consider a retry with temperature 0.

## Smells

### Hand-rolled Azure OpenAI / Anthropic wire models duplicate the official SDKs (severity: medium)
- **Location:** `OpenAgent.LlmText.OpenAIAzure/Models/*` and `OpenAgent.LlmText.AnthropicSubscription/Models/*`
- **Issue:** `ChatCompletionRequest`, `ChatMessage`, `ToolCall`, `ToolCallFunction`, `ChatTool`, `ChatFunction`, `StreamOptions`, `ResponseFormatSpec`, `AnthropicMessagesRequest`, etc. duplicate schemas provided by `Azure.AI.OpenAI` and `Anthropic.SDK`. Maintenance burden grows every API rev (new `tool_choice` shape, new `response_format`, new modalities). The setup-token impersonation needs hand control over headers, but serialization models don't — the SDKs accept custom `HttpMessageHandler`s.
- **Suggestion:** For Anthropic subscription: keep the auth customization but use `Anthropic.SDK` types with a custom handler injecting the Claude Code identity headers per-request. For Azure OpenAI: use `Azure.AI.OpenAI` (chat completions + realtime). Reduces ~600 LOC of model code.

### Duplicated text-provider and voice-session structure across providers (severity: medium)
- **Location:** `AzureOpenAiTextProvider.cs:63-264` vs `AnthropicSubscriptionTextProvider.cs:84-327`; `AzureOpenAiVoiceSession.cs` vs `GrokVoiceSession.cs` (~95% identical)
- **Issue:** Both text providers implement the same lifecycle (persist user, round loop with cap, SSE stream, accumulate, detect tool_calls, persist, execute, re-loop, final persist with token accounting, re-fetch conversation). Both voice sessions implement identical receive loops, tool dispatch, event mapping, and dispose logic. Bug fixes don't propagate automatically.
- **Suggestion:** Extract `TextProviderBase` owning the lifecycle with abstract `StreamRoundAsync(request, ct)` for wire-specific streaming. Extract `OpenAiRealtimeSessionBase` parameterized by endpoint URL, auth header, and event-name map.

### Magic strings for role names and wire event types (severity: low)
- **Location:** Pervasive: `"assistant"`, `"tool"`, `"user"`, `"system"`, `"function"`, `"tool_use"`, `"tool_result"`, `"text_delta"`, `"input_json_delta"`, `"tool_calls"`, `"[DONE]"`
- **Issue:** Typos silent. `"tool"` vs `"tool_result"` in Anthropic paths is easy to confuse.
- **Suggestion:** `MessageRoles` static (User, Assistant, Tool, System) in `OpenAgent.Models`. Voice already did this (`EventTypes` per provider); extend to text streaming.

### Anonymous types for API payloads (severity: low)
- **Location:** `AzureOpenAiVoiceSession.cs:171-176, 249-252`; `GrokVoiceSession.cs:189, 249-252`; `GeminiLiveVoiceSession.cs:465-468`
- **Issue:** CLAUDE.md: "never anonymous types for API payloads". `new { type = "function_call_output", call_id = callId, output = result }` ships to the server; the tool_calls persistence uses anonymous types for a *persisted* payload, which is also an API surface.
- **Suggestion:** Typed `FunctionCallOutputItem` and shared `StoredToolCall` — also covered by the voice-tool-persistence fix above.

### Stale empty project `OpenAgent.Voice.OpenAI/` (severity: low)
- **Location:** `src/agent/OpenAgent.Voice.OpenAI/`
- **Issue:** Only `obj/` exists; no `.cs` / `.csproj`; not referenced anywhere in the solution.
- **Suggestion:** Delete the directory.

### `JsonSerializerOptions` instantiated per-call on two providers (severity: low)
- **Location:** `AzureOpenAiTextProvider.cs:34-35`; `AzureOpenAiRealtimeVoiceProvider.cs:38-39`
- **Issue:** `new JsonSerializerOptions { PropertyNameCaseInsensitive = true }` inline. Anthropic/Grok/Gemini all cache as `static readonly`. Azure text and Azure voice don't — wasted allocation, wasted reflection cache warmup.
- **Suggestion:** Add `static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };` matching the Anthropic convention.

### `StoredToolCall` declared inside the provider file, not in Models (severity: low)
- **Location:** `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:521-543`
- **Issue:** Bottom-of-file type declaration. Convention is models under `Models/`. More importantly, both text providers agree on the persisted tool-call JSON shape (`CLAUDE.md`: "Build persisted tool call format (same as Azure provider)") yet reinvent it each time.
- **Suggestion:** Move to `OpenAgent.Models/Conversations/StoredToolCall.cs`; both providers deserialize via the same type.

### Grok: `conversation.Model` is silently ignored (severity: low)
- **Location:** `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokRealtimeVoiceProvider.cs:36`; `GrokVoiceSession.cs:120-159`
- **Issue:** `Models => []` and the session never references `_conversation.Model`. If a user stored a model string, it's a no-op. Confusing when switching providers and the model field is left stale.
- **Suggestion:** Surface a validation warning in the UI when a Grok conversation has a non-empty `Model`, or document explicitly that the server-side picks.

### No `ConfigureAwait(false)` in library projects (severity: low)
- **Location:** All `LlmText.*`, `LlmVoice.*`, `Compaction` — no usages found.
- **Issue:** Fine for ASP.NET Core (no SynchronizationContext), but the shared `Compaction` / `LlmText` projects could be consumed by a WinForms `ChatCli` or a MAUI shell. All awaits would capture the UI context then.
- **Suggestion:** Enable `CA2007` on library projects; add `ConfigureAwait(false)` where it matters.

### `IVoiceSession.ReceiveEventsAsync` contract ambiguous on multiple readers (severity: low)
- **Location:** `src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs:31`
- **Issue:** The interface doesn't say whether `ReceiveEventsAsync` can be called twice. Implementations use `Channel<T>` with `SingleReader = true`, so a second reader silently races.
- **Suggestion:** Doc comment: "Call at most once per session lifetime."

### `Models` list parsed from comma-separated string via JsonElement peek (severity: low)
- **Location:** `AzureOpenAiTextProvider.cs:44-47`; `AnthropicSubscriptionTextProvider.cs:50-60`; `GeminiLiveVoiceProvider.cs:45-56`
- **Issue:** `Configure` sees a `JsonElement`, calls `JsonSerializer.Deserialize<Config>` (which ignores `Models` via `[JsonIgnore]`), then peeks at the raw `models` property to split by comma. Awkward round-trip — `Configure` has to know the wire shape independently of the DTO.
- **Suggestion:** Custom `StringListJsonConverter` on the `Models` property, or expose a `ModelsCsv` string field and compute the split in a property getter.

## Open Questions
1. Who triggers compaction? I reviewed `CompactionSummarizer` but not the caller. If post-turn and synchronous, it blocks the user response; if async, ordering of subsequent turns vs the compaction write is important.
2. `Conversation.CompactedUpToRowId` is tracked but `BuildChatMessages` / `BuildMessages` pull all stored messages — does compaction prune, or is compaction-plus-live-history double-counted?
3. Does `message_delta` from Anthropic re-emit `input_tokens` that should overwrite the `message_start` value? Current code only reads `output_tokens` from `message_delta` — input count is captured once at start.
4. Grok voice session has no `InputAudioTranscription` — is this because Grok doesn't support user-side transcription, or is it on by default server-side? Need to check Grok's Realtime docs. Listed as a bug under the assumption the client must opt in.
5. `AnthropicMessage.Content` is `object` (`AnthropicRequest.cs:53`) — either `string` or `List<AnthropicContentBlock>`. `System.Text.Json` polymorphic serialization relies on runtime type detection. AOT/trimming-compatible? Might want an explicit `JsonConverter`.
6. The Azure text `ResponseFormat = "json_object"` works on current Azure models — does it work on `gpt-5` deployments, or does Azure require `"json_schema"` with a schema? Compaction will regress the day that flips.
7. Is `Conversation.ReplyToChannelMessageId` ever used on outbound? The Azure text provider decorates messages with `[Reply to Msg: ...]` prefix (`AzureOpenAiTextProvider.cs:387-389`) but the Anthropic provider doesn't. Cross-provider divergence intentional?
8. Should the voice providers attempt reconnect on transient `WebSocketException` (like Gemini does for the 15-min cap)? Current behavior: Azure/Grok silently drop the session on any disconnect, frontend must reinitiate.

## Files reviewed
- `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ChatCompletionRequest.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ChatCompletionResponse.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/AzureOpenAiTextConfig.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ResponseFormatSpec.cs`
- `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`
- `src/agent/OpenAgent.LlmText.AnthropicSubscription/Models/AnthropicConfig.cs`
- `src/agent/OpenAgent.LlmText.AnthropicSubscription/Models/AnthropicRequest.cs`
- `src/agent/OpenAgent.LlmText.AnthropicSubscription/Models/AnthropicResponse.cs`
- `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiRealtimeVoiceProvider.cs`
- `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs`
- `src/agent/OpenAgent.LlmVoice.OpenAIAzure/Models/EventTypes.cs`
- `src/agent/OpenAgent.LlmVoice.OpenAIAzure/Models/RealtimeEnvelope.cs`
- `src/agent/OpenAgent.LlmVoice.OpenAIAzure/Models/RealtimeSessionConfig.cs`
- `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokRealtimeVoiceProvider.cs`
- `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokVoiceSession.cs`
- `src/agent/OpenAgent.LlmVoice.GrokRealtime/Models/GrokEnvelope.cs`
- `src/agent/OpenAgent.LlmVoice.GrokRealtime/Protocol/EventTypes.cs`
- `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceProvider.cs`
- `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs`
- `src/agent/OpenAgent.LlmVoice.GeminiLive/Models/GeminiMessages.cs`
- `src/agent/OpenAgent.Voice.OpenAI/` (stale empty project — flagged)
- `src/agent/OpenAgent/VoiceSessionManager.cs`
- `src/agent/OpenAgent.Contracts/IVoiceSessionManager.cs`
- `src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs`
- `src/agent/OpenAgent.Contracts/ILlmTextProvider.cs`
- `src/agent/OpenAgent.Contracts/ICompactionSummarizer.cs`
- `src/agent/OpenAgent.Contracts/IAgentLogic.cs`
- `src/agent/OpenAgent.Compaction/CompactionSummarizer.cs`
- `src/agent/OpenAgent.Compaction/CompactionPrompt.cs`
- `src/agent/OpenAgent.Models/Common/CompletionEvent.cs`
- `src/agent/OpenAgent.Models/Common/CompletionOptions.cs`
- `src/agent/OpenAgent.Models/Common/ToolResultSummary.cs`
- `src/agent/OpenAgent.Models/Conversations/Message.cs`
- `src/agent/OpenAgent.Models/Conversations/Conversation.cs`
- Cross-referenced: `docs/anthropic-setup-token-auth.md`, `docs/review-voice-providers.md`
