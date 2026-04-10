# OpenAgent Code Review Report

**Date:** 2026-04-10
**Reviewer:** Claude Opus 4.6
**Method:** 3-wave parallel review (11 domains, 7 concurrent agents)
**Prompts:** Generated from `docs/review/code-review-prompts.md`

---

## Executive Summary

The architecture is sound — IAgentLogic stays as injected context, lazy provider resolution works correctly, system prompt composition avoids cross-conversation contamination by design, and the CompletionEvent hierarchy provides clean universal output. The codebase is well-structured with clear separation of concerns.

The main themes requiring attention are:

1. **Security boundary bypasses** — path prefix checks, SSRF via DNS rebinding/redirects
2. **Thread safety in singletons** — mutable state shared across concurrent requests
3. **Missing error handling in LLM providers** — tool execution failures kill streams
4. **Frontend error swallowing** — API errors silently ignored, no error boundaries

80+ findings across 3 review waves, categorized below by severity.

---

## Critical Findings

### C1. TOCTOU DNS Rebinding in WebFetch
**Files:** `WebFetchTool.cs:48-63`, `WebFetchToolHandler.cs:14`
**Impact:** SSRF — attacker-controlled DNS returns safe IP for validation, then 127.0.0.1 for the actual fetch.

DNS is validated first via `UrlValidator.ValidateWithDnsAsync`, then `HttpClient.SendAsync` performs its own DNS resolution. An attacker's DNS server can return different IPs on successive queries (DNS rebinding).

Additionally, the default `HttpClient` follows HTTP 302 redirects automatically. A server at a public IP can redirect to `http://169.254.169.254/latest/meta-data/` (cloud metadata) or any internal service.

**Fix:** Use a custom `SocketsHttpHandler` with a `ConnectCallback` that resolves DNS once and validates the IP before connecting. This eliminates the TOCTOU gap entirely. Also disable `AllowAutoRedirect` or validate redirect targets at the socket level.

### C2. Path Prefix Bypass in FileSystem Tools and Shell cwd
**Files:** `FileReadTool.cs:38`, `FileWriteTool.cs:39`, `FileAppendTool.cs:38`, `FileEditTool.cs:42`, `ShellExecTool.cs:73`
**Impact:** Arbitrary file read/write/execute outside the data directory sandbox.

All path validation uses:
```csharp
if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
```

If `basePath` is `/home/data` (no trailing separator), then `/home/data-evil/secrets` passes the check. `Path.GetFullPath` does not guarantee a trailing separator.

**Fix:** Normalize basePath to always end with the directory separator:
```csharp
var normalizedBase = basePath.EndsWith(Path.DirectorySeparatorChar)
    ? basePath : basePath + Path.DirectorySeparatorChar;
if (!fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase)
    && fullPath != basePath)
```

### C3. Tool Execution Exceptions Kill the Stream
**Files:** `AzureOpenAiTextProvider.cs:206`, `AnthropicSubscriptionTextProvider.cs:265`
**Impact:** Orphaned tool calls in conversation history; partial multi-tool execution leaves invisible results.

If `ExecuteToolAsync` throws in either text provider, the tool result is never persisted and never sent back to the LLM. The exception kills the stream. The assistant tool-call message is already persisted, creating an orphaned tool call. In multi-tool rounds, earlier successful tool results become persisted but invisible.

The voice session (`AzureOpenAiVoiceSession.cs:259-276`) already handles this correctly with try-catch.

**Fix:** Wrap `ExecuteToolAsync` in both text providers in a try-catch that captures the error as a JSON result (e.g., `{"error": "tool execution failed: <message>"}`), persists it, and continues the loop — matching the voice session pattern.

### C4. WhatsApp Dedup Dictionary Used Concurrently
**File:** `WhatsAppMessageHandler.cs:29`
**Impact:** Data corruption — `Dictionary<string, DateTime>` accessed from concurrent `Task.Run` calls.

The `_processedMessages` dictionary is a plain `Dictionary`, not `ConcurrentDictionary`. `HandleMessageAsync` is called via `Task.Run` from `HandleNodeEvent`, so multiple concurrent messages can invoke `TryRecordMessage` simultaneously. The eviction logic also iterates and mutates the dictionary.

**Fix:** Replace with `ConcurrentDictionary<string, DateTime>`, or protect `TryRecordMessage` with a lock (simpler given the iteration-with-mutation in eviction).

### C5. Double-Dispose Race in Terminal Sessions
**Files:** `ProcessTerminalSession.cs:126-147`, `PtyTerminalSession.cs:158`
**Impact:** `Process.Kill()` + `Process.Dispose()` called twice — throws `InvalidOperationException`.

The `_disposed` field is `volatile bool` but there is no atomic exchange. Two concurrent calls to `DisposeAsync` can both read `_disposed` as `false` and proceed.

**Fix:** Use `Interlocked.Exchange`:
```csharp
private int _disposed;
if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
```

### C6. PTY Master File Descriptor Not Wrapped in SafeHandle
**File:** `PtyTerminalSession.cs`
**Impact:** File descriptor leak on unhandled exceptions or ungraceful shutdown.

The master fd is stored as a raw `int _masterFd`. If `DisposeAsync` is never called, the fd leaks permanently.

**Fix:** Create a `PtyMasterSafeHandle : SafeHandle` that calls `PtyInterop.close(fd)` in `ReleaseHandle()`.

---

## High-Priority Findings

### H1. Symlink Traversal in FileSystem Tools
**Files:** All FileSystem tools
**Impact:** Arbitrary file access outside sandbox via symlinks created by shell_exec.

None of the file tools check whether the resolved path involves symbolic links. A symlink inside `dataPath` pointing outside it bypasses the prefix check.

**Fix:** After the `StartsWith` check, resolve the real path via `File.ResolveLinkTarget` with `returnFinalTarget: true` and re-validate.

### H2. No Constant-Time API Key Comparison
**File:** `ApiKeyAuthenticationHandler.cs:40`
**Impact:** Timing side-channel for API key recovery.

`string.Equals` with `StringComparison.Ordinal` short-circuits on the first differing byte.

**Fix:** Use `CryptographicOperations.FixedTimeEquals` with UTF-8 encoded bytes.

### H3. Unrestricted PTY Shell Access
**File:** `WebSocketTerminalEndpoints.cs:64`
**Impact:** Any API key holder gets full interactive shell access to the host.

The terminal endpoint spawns a real shell with the server's full privileges. Unlike `shell_exec` which constrains `cwd`, the terminal has no restrictions once the shell is running.

**Fix:** Consider gating behind a separate admin key, validating `sessionId` format, and/or using namespace isolation on Linux.

### H4. SystemPromptBuilder.Reload() Not Thread-Safe
**File:** `SystemPromptBuilder.cs:44-48`
**Impact:** Concurrent `Build()` calls during `Reload()` see partial/empty dictionary.

`SystemPromptBuilder` is a singleton. `Reload()` calls `_files.Clear()` then `LoadFiles()`, mutating a `Dictionary<string, string>` that `Build()` reads concurrently.

**Fix:** Build a new dictionary in `LoadFiles` and swap atomically:
```csharp
public void Reload()
{
    _files = LoadFiles(_dataPath); // returns new Dictionary
    _skillCatalog.Reload();
}
```
Mark `_files` as `volatile` or use `Interlocked.Exchange`.

### H5. AgentConfig Mutable Singleton Without Memory Barriers
**File:** `AgentConfig` (registered as singleton in Program.cs)
**Impact:** Stale reads on ARM platforms; potential for inconsistent provider/model combination.

Properties like `TextProvider`, `TextModel`, `MemoryDays` are written by `AgentConfigConfigurable.Configure()` (from admin API) and read concurrently by channel handlers and WebSocket endpoints.

**Fix:** Use `volatile` backing fields or an immutable snapshot pattern with atomic swap.

### H6. LLM Provider Configure() Race with In-Flight Requests
**Files:** `AnthropicSubscriptionTextProvider.cs:62`, `AzureOpenAiTextProvider.cs:51`
**Impact:** `_httpClient?.Dispose()` followed by creating a new one — concurrent `CompleteAsync` could use the disposed client.

**Fix:** Use `Interlocked.Exchange` to swap the old client and dispose it after.

### H7. Voice Session Has No Tool-Call Round Safety Cap
**File:** `AzureOpenAiVoiceSession.cs`
**Impact:** A misbehaving LLM could loop tool calls indefinitely in voice mode.

The text providers have `const int maxToolRounds = 10`. The voice session has no equivalent counter.

**Fix:** Add a `_toolCallRoundCount` field that resets on user speech and disconnects if it exceeds a threshold.

### H8. WhatsApp State Machine Gaps
**File:** `WhatsAppChannelProvider.cs`
**Impact:** Provider stuck in `Pairing` state if Node process crashes mid-pairing; no recovery from `Failed` state.

- Node process crash during pairing: stdout EOF exits silently, no disconnect event generated. Provider stays in `Pairing` forever.
- TOCTOU race in `GetQrAsync`/`StartPairingAsync` can spawn duplicate Node processes.
- No transition from `Failed` back to `Unpaired` — requires full provider restart.

**Fix:** Add a pairing timeout. Allow re-entry from `Failed` state in `StartPairingAsync` with reconnect counter reset. Make `StartPairingAsync` fully idempotent.

### H9. WhatsApp Outbound Silently Drops Messages
**File:** `WhatsAppChannelProvider.cs:205-211`
**Impact:** Messages queued to a dead Node process are silently lost.

`TryWrite` on the unbounded channel always succeeds, even if the Node process has crashed. The caller receives no error.

**Fix:** Check `_nodeProcess.IsRunning` before writing. Throw if the process is not running.

### H10. TelegramBotClientSender Leaks HttpClient
**File:** `TelegramBotClientSender.cs:23`
**Impact:** Socket leak on stop/restart cycles.

`HttpClient` created in constructor but the class does not implement `IDisposable`. Old senders abandoned on restart.

**Fix:** Implement `IDisposable` and dispose the HttpClient, or use `IHttpClientFactory`.

### H11. FindOrCreateChannelConversation Race Condition
**File:** `SqliteConversationStore.cs:166-217`
**Impact:** Duplicate conversations for the same external chat under concurrent load.

No unique index on `(ChannelType, ConnectionId, ChannelChatId)`. Two concurrent handlers can both pass the find check and both INSERT.

**Fix:** Add a unique index on the three channel columns and use `INSERT OR IGNORE` + re-read.

### H12. Compaction Lock Not Crash-Safe
**File:** `SqliteConversationStore.cs:400-426`
**Impact:** `CompactionRunning` flag stuck permanently if process crashes during compaction.

**Fix:** On startup, reset stale flags:
```sql
UPDATE Conversations SET CompactionRunning = 0 WHERE CompactionRunning = 1;
```

### H13. GetMessagesByIds Has No Conversation Scoping
**File:** `SqliteConversationStore.cs:368-389`
**Impact:** Cross-conversation message leak via ExpandTool.

The query fetches messages globally by ID with no `ConversationId` filter. Message IDs are GUIDs so not trivially exploitable, but violates least privilege.

**Fix:** Add a `conversationId` filter to the query.

### H14. Compaction May Split Tool Call/Result Pairs
**File:** `SqliteConversationStore.cs:428-451`
**Impact:** Tool results silently dropped — neither in compaction summary nor in live context.

The boundary calculation doesn't account for multi-message tool call rounds.

**Fix:** After computing the tentative cutoff, scan to ensure it doesn't land mid-round. Pull back to before the assistant tool_call message if needed.

### H15. XML Attribute Injection in Active Skill Tags
**File:** `SystemPromptBuilder.cs:119`
**Impact:** Malformed system prompt from skill names containing quote characters.

```csharp
$"<active_skill name=\"{skill.Name}\" directory=\"{relativeSkillDir}\">"
```

The `BuildCatalogPrompt` method uses `EscapeXml`, but this site does not. `EscapeXml` also doesn't escape `"` for attributes.

**Fix:** Apply XML escaping (including `&quot;`) to both `skill.Name` and `relativeSkillDir`.

### H16. ActiveSkills Race Condition
**File:** `SkillToolHandler.cs:57-80`
**Impact:** Lost updates when two messages modify ActiveSkills concurrently.

Read-modify-write without locking or optimistic concurrency.

**Fix:** Add per-conversation locking via `ConcurrentDictionary<string, SemaphoreSlim>`.

### H17. WebSocket Text Endpoint Doesn't Handle Messages >8KB
**File:** `WebSocketTextEndpoints.cs:74-87`
**Impact:** Truncated JSON parse on large user messages.

The buffer is 8192 bytes. `EndOfMessage` is not checked — large messages are silently truncated.

**Fix:** Accumulate frames until `EndOfMessage` is true:
```csharp
using var ms = new MemoryStream();
WebSocketReceiveResult result;
do {
    result = await ws.ReceiveAsync(buffer, ct);
    ms.Write(buffer, 0, result.Count);
} while (!result.EndOfMessage);
```

### H18. AdminEndpoints Masked Values Overwrite Real Secrets
**File:** `AdminEndpoints.cs:79-108`
**Impact:** Round-tripping a GET response via POST replaces real API keys with literal `"***"`.

**Fix:** Skip `"***"` values in the merge loop:
```csharp
if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() == "***")
    continue;
```

### H19. Missing Private IP Ranges in SSRF Protection
**File:** `UrlValidator.cs:79-113`
**Impact:** SSRF to cloud internal networks.

Missing: `100.64.0.0/10` (Carrier-Grade NAT, used by cloud providers), `192.0.0.0/24`, `198.18.0.0/15`, `240.0.0.0/4`, `255.255.255.255`.

**Fix:** Add these ranges to `IsPrivateOrReserved`.

### H20. Flat Privilege Model
**File:** `AdminEndpoints.cs`
**Impact:** Same API key for chat and credential-writing operations.

No separation between "use the agent" and "administer the agent." A compromised chat client can reconfigure providers.

**Fix:** Introduce a separate admin key or role-based authorization.

### H21. No Audit Logging of Admin Config Changes
**File:** `AdminEndpoints.cs:79-109`
**Impact:** No trail of who changed what provider configuration and when.

**Fix:** Add structured logging for all config mutations.

### H22. ActiveBridges TOCTOU Race in Terminal Endpoint
**File:** `WebSocketTerminalEndpoints.cs:51-56, 78-79, 98`
**Impact:** The `finally` block can remove a newer connection's CTS entry.

**Fix:** Use compare-and-remove:
```csharp
ActiveBridges.TryRemove(new KeyValuePair<string, CancellationTokenSource>(sessionId, bridgeCts));
```

### H23. WebSocket Disconnect Does NOT Kill PTY Process
**File:** `WebSocketTerminalEndpoints.cs:96`
**Impact:** PTY processes linger forever after disconnect.

**Fix:** Add an idle timeout to `TerminalSessionManager` that closes sessions with no active bridge.

### H24. No React Error Boundaries
**File:** Entire `src/web/src/`
**Impact:** Any unhandled exception in one app crashes the entire React tree.

**Fix:** Add per-window `ErrorBoundary` components inside `WindowFrame`.

### H25. Frontend API Functions Never Check `res.ok`
**Files:** `settings/api.ts`, `conversations/api.ts`
**Impact:** HTTP 401/403/500 responses parsed as expected types — confusing runtime errors.

**Fix:** Create a wrapper around `apiFetch` that throws on non-OK responses.

### H26. Frontend Error Swallowing
**Files:** `SettingsApp.tsx:24`, `AgentConfigForm.tsx:63`, `ConnectionsForm.tsx:247`
**Impact:** Network failures produce empty UI with no error indication.

`.catch(() => {})` and missing `.catch()` handlers throughout.

**Fix:** Add error state management and surface failures to the user.

### H27. Query String Token Not Stripped From URL
**File:** `auth/token.ts:17-23`
**Impact:** API key persists in browser history and server logs.

The hash fragment path is stripped but the query string `?token=xxx` is not.

**Fix:** Call `window.history.replaceState` after extracting the query token.

### H28. _lastPongTime Thread Safety
**File:** `WhatsAppChannelProvider.cs:49`
**Impact:** Potential stale reads on non-x86 platforms.

`DateTime` written from event handler, read from timer callback, with no memory barrier.

**Fix:** Use `Volatile.Read`/`Volatile.Write` or store ticks in a `long` with `Interlocked`.

---

## Medium-Priority Findings

### M1. ScheduledTaskService Concrete Type Across Project Boundary
**File:** `ScheduledTaskEndpoints.cs` references concrete `ScheduledTaskService`
**Fix:** Extract `IScheduledTaskService` to `OpenAgent.Contracts`.

### M2. SkillCatalog Concrete Type in SystemPromptBuilder
**File:** `SystemPromptBuilder.cs:18`
**Fix:** Extract `ISkillCatalog` to `OpenAgent.Contracts`.

### M3. Tool Handler Registration Inconsistency
**File:** `Program.cs:68-71`
`AddScheduledTasks()` extension exists but FileSystem, Shell, WebFetch, Expand, Skills are registered inline.
**Fix:** Each tool project should expose an `AddXxx()` extension method.

### M4. Anonymous Types in Serialization
**Files:** `ExpandToolHandler.cs:46-54`, `SkillToolHandler.cs` (throughout), `ScheduledTaskToolHandler.cs` (throughout), `ChatEndpoints.cs:59-62`
Convention violation: CLAUDE.md requires `[JsonPropertyName]` attributes on serialized models.
**Fix:** Create named record types for API responses. Tool-internal results can remain as-is.

### M5. FileExplorer `/content` Has No File Size Limit
**File:** `FileExplorerEndpoints.cs:81`
`ReadToEnd()` with no size guard — OOM on large files.
**Fix:** Add a file size check (e.g., 10MB cap).

### M6. LogEndpoints `limit` Has No Upper Bound
**File:** `LogEndpoints.cs:119`
**Fix:** Cap to a reasonable maximum (e.g., 10000).

### M7. LogEndpoints Path Traversal Check Missing Case-Insensitive Comparison
**File:** `LogEndpoints.cs:74`
`StartsWith` without `StringComparison.OrdinalIgnoreCase` on Windows.
**Fix:** Add `StringComparison.OrdinalIgnoreCase`.

### M8. ScheduledTaskEndpoints Unbounded Webhook Body
**File:** `ScheduledTaskEndpoints.cs:108`
`ReadToEndAsync` with no size limit.
**Fix:** Cap body size (e.g., 64KB).

### M9. API Key in Query String
**File:** `ApiKeyAuthenticationHandler.cs:30-32`
Query strings are logged by web servers, visible in browser history.
**Fix:** For WebSocket, consider a short-lived session token exchange.

### M10. Terminal No Reconnect in Frontend
**File:** `TerminalApp.tsx:105-109`
WebSocket close shows `[Connection closed]` with no reconnect option.
**Fix:** Add a reconnect button or automatic reconnect with backoff.

### M11. ConversationType Enum Divergence from CLAUDE.md
**File:** `Conversation.cs:5-9`
CLAUDE.md documents `ScheduledTask` and `WebHook` but the enum only has `Text` and `Voice`.
**Fix:** Update CLAUDE.md to clarify these are not yet on master.

### M12. AdminEndpoints XML Doc Comments Inside Method Body
**File:** `AdminEndpoints.cs:34-37, 46-49`
`///` doc comments inside the method body are dead code.
**Fix:** Convert to regular `//` comments or extract to named methods.

### M13. ConnectionEndpoints Empty Catch Block
**File:** `ConnectionEndpoints.cs:119`
Swallows restart exceptions silently — no logging.
**Fix:** Add logging.

### M14. SkillCatalog._skills Not Volatile
**File:** `SkillCatalog.cs:14`
Dictionary reference swapped on `Reload()` without memory barrier.
**Fix:** Mark as `volatile` or use `Interlocked.Exchange`.

### M15. No Catalog Description Length Enforcement
**File:** `SkillDiscovery.cs:71`
Documented 1024-char limit not enforced. 200 skills with long descriptions could overflow context.
**Fix:** Truncate descriptions to 1024 chars in `Scan`.

### M16. ActivateSkillResourceTool Does Not Verify Skill Is Active
**File:** `SkillToolHandler.cs:200-229`
Loads resources for any cataloged skill, not just active ones.
**Fix:** Check `conversation.ActiveSkills` before loading.

### M17. Ping Timer Not Disposed on Failed State
**File:** `WhatsAppChannelProvider.cs`
Timer continues firing with no effect after max reconnects exhausted.
**Fix:** Dispose the ping timer when entering `Failed` state.

### M18. Webhook Handler Uses CancellationToken.None
**File:** `TelegramWebhookEndpoints.cs:70`
Long-running LLM completions continue after shutdown begins.
**Fix:** Pass a cancellation token from the provider that is cancelled during `StopAsync`.

### M19. Streaming Draft Delivery Failure Not Surfaced
**File:** `TelegramMessageHandler.cs:231-319`
If connection drops mid-stream, assistant response is saved but never delivered. No recovery mechanism.
**Fix:** Log a specific "delivery failed" event. Consider a retry queue.

### M20. HttpResponseMessage Not Disposed in Provider Tool-Call Loop
**Files:** Both text providers
Up to 10 undisposed response objects per request.
**Fix:** Wrap `httpResponse` in `using`.

### M21. Voice Session Task.Run Unobserved Exception
**File:** `AzureOpenAiVoiceSession.cs:239`
If `SendToolResultAndContinueAsync` throws, the exception goes unobserved.
**Fix:** Add an outer try-catch that logs the error.

### M22. Compaction Context System Message in Anthropic Provider
**File:** `AnthropicSubscriptionTextProvider.cs` BuildMessages
The synthetic system message from compaction context would be sent as a messages-array entry, but Anthropic doesn't accept `system` role there.
**Fix:** Check for the synthetic context message and inject into the system prompt instead.

### M23. ActiveSkills Deserialization Doesn't Handle Malformed JSON
**File:** `SqliteConversationStore.cs:554`
Malformed JSON in the column makes the entire conversation inaccessible.
**Fix:** Wrap in try-catch, return `null` on failure with a warning log.

---

## Test Coverage Priorities

The following production areas have zero test coverage. Ordered by risk based on confirmed bugs found during this review.

| Priority | Area | Risk | Test Type | Key Bugs Caught |
|----------|------|------|-----------|-----------------|
| 1 | FileSystemToolHandler path traversal | Critical | Unit | C2 path prefix bypass, H1 symlink escape |
| 2 | ShellExecTool cwd bypass + timeout | Critical | Unit | C2 path prefix bypass, timeout enforcement |
| 3 | AdminEndpoints secret masking | High | Integration | H18 masked value overwrite |
| 4 | SystemPromptBuilder | High | Unit | H4 Reload thread safety, H15 XML injection |
| 5 | WebSocketTerminalEndpoints eviction | High | Integration | H22 ActiveBridges TOCTOU race |
| 6 | DeliveryRouter | Medium | Unit | H9 offline channel, WebSocket path |
| 7 | CompactionSummarizer | Medium | Unit | H14 boundary splitting, incremental compaction |
| 8 | AgentLogic tool routing | Medium | Unit | Missing tool error format |
| 9 | PtyTerminalSession | Low | Unit (Linux) | C5 double-dispose race |

### Recommended Test Cases

**FileSystem path traversal (3 tests):**
1. Path to sibling directory (`/tmp/data-evil/` when basePath is `/tmp/data`) — catches C2
2. Classic `../../etc/passwd` traversal
3. Symlink inside basePath pointing outside — catches H1

**Shell cwd bypass (3 tests):**
1. cwd resolving to sibling workspace directory — catches C2
2. Long-running command with 1-second timeout — validates kill path
3. Command writing to both stdout and stderr — validates merge

**AdminEndpoints (3 tests):**
1. POST masked `"***"` value back, verify real secret preserved — catches H18
2. Partial merge preserves unsubmitted fields
3. Unknown provider key returns 404

**SystemPromptBuilder (3 tests):**
1. ConversationType filtering (Text excludes VOICE.md, Voice includes it)
2. Active skills injection with quote characters in name — catches H15
3. Concurrent Reload/Build stress test — catches H4

---

## Architectural Observations

### What Was Done Well
- **IAgentLogic contract** is clean — stays as injected context, never orchestrates
- **Lazy provider resolution** correctly wraps keyed service in `Func<>`, resolved per-message
- **System prompt composition** takes `activeSkills` as a parameter, producing per-conversation prompts from per-call arguments — no cross-contamination
- **Orphaned tool call detection** in `BuildChatMessages`/`BuildMessages` is thorough — correctly skips incomplete rounds
- **Telegram webhook secret** uses `CryptographicOperations.FixedTimeEquals` for constant-time comparison
- **TerminalApp cleanup** is exemplary: xterm disposal, ResizeObserver disconnect, debounce timer clear, StrictMode guard
- **No `dangerouslySetInnerHTML`** anywhere in the frontend
- **Per-chat conversation IDs** use separate database columns, eliminating colon-collision risk
- **PtyInterop** correctly avoids `forkpty` (unsafe from managed .NET) and uses the safe `posix_openpt` sequence

### Thread Safety Pattern (Recurring Theme)
Multiple singletons share the pattern of mutable state without synchronization:
- `SystemPromptBuilder._files` (Dictionary)
- `AgentConfig` properties
- `SkillCatalog._skills` (Dictionary)
- Both text providers' `_config` and `_httpClient`
- `WhatsAppMessageHandler._processedMessages` (Dictionary)
- `WhatsAppChannelProvider._lastPongTime` (DateTime)

Consider establishing a project-wide pattern: either immutable snapshots with atomic swap, or `volatile`/`Interlocked` for simple fields.

### Missing Interfaces (Coupling)
Concrete types crossing project boundaries without interfaces in `OpenAgent.Contracts`:
- `ScheduledTaskService` → needs `IScheduledTaskService`
- `SkillCatalog` → needs `ISkillCatalog`
- `SystemPromptBuilder` → would benefit from `ISystemPromptBuilder`
- `DeliveryRouter` → internal, lower priority

### Registration Inconsistency
`AddScheduledTasks()` is the model extension method. These should follow suit:
- `OpenAgent.Tools.FileSystem` → `AddFileSystemTools()`
- `OpenAgent.Tools.Shell` → `AddShellTools()`
- `OpenAgent.Tools.WebFetch` → `AddWebFetchTools()`
- `OpenAgent.Tools.Expand` → `AddExpandTools()`
- `OpenAgent.Skills` → `AddSkills()`
