# OpenAgent Codebase Review — Claude Sonnet 4.6

Generated 2026-04-10 via 11 parallel review agents covering all domains.

---

## Critical — Fix immediately (data corruption, crash, or silent data loss)

| # | Area | Finding |
|---|------|---------|
| C1 | Storage | `BuildChatMessages`/`BuildMessages`: `HashSet` iteration order is undefined — when a tool-call round has 2+ tool calls, wrong results are silently paired with wrong calls and sent to the LLM. Affects both Azure and Anthropic providers. |
| C2 | Storage | Malformed `ActiveSkills` JSON in DB throws `JsonException` through `Get`, `GetAll`, `GetOrCreate`, `FindChannelConversation` — the conversation becomes permanently unrecoverable. |
| C3 | Voice | `ReceiveLoopAsync`: unhandled `JsonException` from one bad frame tears down the entire voice receive loop silently. |
| C4 | WhatsApp | `UnsafeRelaxedJsonEscaping` emits literal `\n` bytes in message text, breaking the stdin/stdout JSON-line framing protocol with the Baileys bridge. |
| C5 | WhatsApp | Dedup `Dictionary<string, DateTime>` accessed concurrently from fire-and-forget `Task.Run` handlers — data race, no lock. |
| C6 | WhatsApp | Concurrent reconnect triggers (ping timer + `disconnected` event) race: two `StartNodeProcessAsync` calls clobber `_nodeProcess` and leak a Node process. |
| C7 | WhatsApp | Process crash during pairing leaves `_state = Pairing` forever — no `Process.Exited` handler, `_qrReady` never completes, no recovery path. |

---

## High — Security vulnerabilities

| # | Area | Finding |
|---|------|---------|
| S1 | WebFetch | SSRF via DNS rebinding: validation resolves DNS, then `HttpClient` re-resolves at send time — a second lookup can return a private IP. Classic TOCTOU. |
| S2 | FileSystem | All tools: `StartsWith(basePath)` without trailing separator — `/home/data2` passes check against base `/home/data`. Symlinks not resolved before the check. |
| S3 | Skills | `<active_skill name="...">` attribute built by string interpolation — a skill name containing `"` produces malformed XML (injection). |
| S4 | Terminal | PTY process never killed on WebSocket disconnect — `CloseAsync` is never called from the WS `finally` block. Bash processes accumulate until `MaxSessions` (4) is hit. |
| S5 | API key auth | `string.Equals` comparison — not constant-time, vulnerable to timing side-channel. |
| S6 | Frontend | Zero React error boundaries — any unhandled render error unmounts the entire app, presenting a blank screen in production. |
| S7 | Endpoints | Webhook body in `ScheduledTaskEndpoints` (and `ScheduledTaskExecutor`) has no size limit — unbounded LLM token injection / OOM. |

---

## Medium — Correctness and reliability issues

### LLM Providers

- `HttpResponseMessage`/`HttpRequestMessage` not disposed in both text providers — leak on caller cancellation
- Voice `Task.Run` swallows `OperationCanceledException` — tool result never sent back, voice session hangs
- SSE chunk `JsonException` not caught in either text provider — unhandled exception with no context
- `AnthropicThinking` missing `budget_tokens` field — API may reject or silently disable thinking

### Storage

- `TryAddColumn` swallows all `SqliteException` types (not just "duplicate column") — schema errors are silent
- Compaction boundary can split a tool call round — tool outcome disappears from context
- No per-conversation write serialization — concurrent channel providers can overwrite each other's updates
- `GetMessagesByIds` has no conversation scope check — can retrieve messages from any conversation by GUID

### Channels

- Telegram: `RequestAborted` token passed to `SendFinalResponseAsync` in webhook mode — final reply silently lost when HTTP connection closes
- WhatsApp: `SendMessageAsync` writes to the stdin of a potentially dead process without checking `State`

### Architecture

- `ScheduledTask`/`WebHook` conversation types receive no `TOOLS.md` — agent may not know what tools it has
- Non-keyed `ILlmTextProvider` singleton hardcoded to Azure — latent trap for any new code that injects it without using the keyed API

### Terminal & Tasks

- `TIOCSWINSZ` constant hardcoded to `0x5414` — wrong on ARM Linux (correct value: `0x80087468`)
- `SemaphoreSlim.Wait()` called from async context in `TerminalSessionManager.Create` — thread pool starvation under load
- `ScheduledTaskStore.Load` doesn't catch `JsonException` — corrupted `scheduled-tasks.json` crashes startup
- `DeliveryRouter` has no interface — cannot inject a fake in unit tests

### API Endpoints

- `FileExplorerEndpoints.ResolveSafePath`: same `StartsWith` boundary false-positive as FileSystem tools
- `LogEndpoints`: `DateTimeOffset.Parse` on `since`/`until` throws 500 on invalid input; no cap on `limit` parameter
- WebSocket text endpoint: messages over 8 KB silently truncated (`EndOfMessage` not checked)
- `ConnectionEndpoints` PUT: restart exception silently swallowed, not logged
- `ScheduledTaskEndpoints`: client-controlled `Id` in POST body with no server-side generation or validation

### Skills

- Quoted scalar values (`name: "my-skill"`) not unquoted — literal `"` appears in catalog XML and activation lookups
- `activate_skill_resource` path containment check missing trailing separator (same pattern as FileSystem)
- `ActiveSkills` activation is a non-atomic read-modify-write — race under concurrent inbound messages
- Description length limit documented but not enforced — oversized descriptions bloat system prompt

### Frontend

- Query-string token never stripped from URL after reading — remains in browser history
- `useVoiceSession`: `playQueueRef` not flushed on unmount — `DOMException` when `source.onended` fires after `AudioContext` is closed
- Settings/Connections: multiple API errors silently swallowed with no UI feedback (`listProviders`, `handleToggle`, `handleDelete`)
- `TerminalApp`: no reconnect logic after session eviction

---

## Low / Consistency

### Coupling & Conventions

- `ISkillCatalog` missing from Contracts; `SkillCatalog` crosses project boundary as concrete type
- Only `AddScheduledTasks()` has an extension method — FileSystem, Shell, WebFetch, Expand, Skills registered inline in `Program.cs`
- Anonymous types in `JsonSerializer.Serialize` in `ExpandTool`, `SkillToolHandler` (6 sites), `ScheduledTaskToolHandler` (4 sites), `ChatEndpoints` — violates `[JsonPropertyName]`-on-named-models convention
- `AdminEndpoints`: XML doc comments inside method body are dead code (not processed by compiler)
- `AdminEndpoints`: Flat privilege model — credential-writing `POST /{key}/config` uses the same key as all read operations

### Security (lower priority)

- Shell tool: no ceiling on `timeout` parameter — LLM can pass `Int32.MaxValue`
- Shell tool: `cwd` restricts starting directory only, not what shell commands can reach (inherent, but undocumented)
- `FileAppendTool`: no file size cap (while `FileWriteTool` has one)
- `FileWriteTool`: content-length check is in chars, not bytes — Unicode content can exceed the byte limit
- WebFetch: `ReadAsStringAsync` called before `MaxResponseBytes` cap — full response loaded into memory first
- Terminal endpoint: `sessionId` route parameter not validated for format/length before passing to session manager
- `LogEndpoints`/`FileExplorerEndpoints`: returns HTTP 403 for path traversal attempts (should be 400)

### Reliability

- Telegram: `webhookSecret` regenerated each restart — fragile validation window at startup
- `AgentLogic`: `Tools` property allocates a new `List` on every call — cache at construction time
- `AgentLogic`: TODO at line 21 (channel-specific prompt variants) — current uniform fallback is correct but undocumented
- PTY/Process dispose: `CancellationTokenSource` with timeout not disposed — leaks a timer
- Interval scheduling: uses `DateTimeOffset.UtcNow` as anchor for next tick — drifts by up to 30 s per cycle (should use `task.State.NextRunAt`)
- `SkillCatalog.Reload()` does a non-atomic field replacement — use `Volatile.Write` or `Interlocked.Exchange`
- Storage: fresh DB performs 17 redundant `ALTER TABLE` calls on first run (swallowed, but noisy)
- `FindOrCreateChannelConversation` always stores `ActiveSkills` as `DBNull`, ignoring any pre-populated value
- `CompactionSummarizer`: raw LLM response not logged before `JsonDocument.Parse` — hard to debug non-JSON refusals
- `DeliveryRouter`: offline channel logs "does not support outbound" instead of "channel is offline"
- `WebSocketTextEndpoints`: second connection to same conversation ID orphans the first socket instead of closing it

---

## Test Coverage Gaps (highest-risk untested areas)

| Area | Risk | Recommended test type |
|------|------|-----------------------|
| `AdminEndpoints` — secret masking on GET | High | Integration (WebApplicationFactory) |
| `DeliveryRouter` — offline channel silent drop | High | Unit (all collaborators are interfaces) |
| `ShellExecTool` — timeout enforcement, process tree kill | High | Unit (real `Process` with `sleep`) |
| `FileSystemToolHandler` — path traversal rejection | High | Unit (temp dir as basePath) |
| `WebSocketTerminalEndpoints` — bridge eviction race | High | Integration |
| `CompactionSummarizer` — incremental round-trip, malformed LLM response | Medium | Unit (fake `ILlmTextProvider`) |
| `SystemPromptBuilder` — per-ConversationType filtering, skill injection | Medium | Unit (temp dir with fixture files) |
| `AgentLogic` — tool routing, unknown tool fallback | Medium | Unit (`InternalsVisibleTo`) |
| `PtyTerminalSession` — dispose does not deadlock | Medium | Integration (Linux only, `[OSSkipCondition]`) |

---

## What to fix first

Three issues stand out as fix-now regardless of other prioritization:

**C1 — HashSet ordering bug** (`BuildChatMessages` / `BuildMessages`)
Silently sends wrong tool results to the LLM today, on every conversation with 2+ parallel tool calls. One-line fix per provider: iterate `storedMessages` in order instead of iterating `expectedIds` (a `HashSet`).

**C2 — ActiveSkills JSON crash**
Any bad write to the `ActiveSkills` column makes a conversation permanently unrecoverable. Wrap `JsonSerializer.Deserialize<List<string>>` in a try/catch with a fallback to `null`.

**C4 — WhatsApp \n framing bug**
Any message containing a newline character corrupts the Baileys bridge JSON-line protocol. Switch the `JsonSerializerOptions` for stdin writes from `UnsafeRelaxedJsonEscaping` to the default encoder, which escapes `\n` as `\\n`.
