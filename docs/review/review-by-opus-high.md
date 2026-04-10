# Code Review: High-Severity Findings

Reviewed by Claude Opus 4.6 on 2026-04-10. Covers all 11 review prompts from `code-review-prompts.md`.

17 high-severity findings across 8 domains.

---

## 1. Security & Trust Boundary

### S-3.1 TOCTOU DNS rebinding in WebFetch

**File:** `OpenAgent.Tools.WebFetch/WebFetchTool.cs:48-63`

`ValidateWithDnsAsync` resolves the hostname and checks all IPs are non-private. Then `httpClient.SendAsync` resolves DNS again via the OS resolver. Between these two calls, a DNS record can change (DNS rebinding attack). A malicious server returns a public IP on first lookup (passing validation), then `127.0.0.1` on the second lookup (used by HttpClient), achieving SSRF to loopback or cloud metadata (`169.254.169.254`).

**Fix:** Use a custom `SocketsHttpHandler` with a `ConnectCallback` that validates the resolved IP at connection time:

```csharp
var handler = new SocketsHttpHandler
{
    ConnectCallback = async (context, ct) =>
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        foreach (var ip in addresses)
            if (IsPrivateOrReserved(ip))
                throw new HttpRequestException("Resolved to private IP");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, ct);
        return new NetworkStream(socket, ownsSocket: true);
    }
};
```

---

### S-4.1 Non-constant-time API key comparison

**File:** `OpenAgent.Security.ApiKey/ApiKeyAuthenticationHandler.cs:40`

`string.Equals(providedKey, Options.ApiKey, StringComparison.Ordinal)` short-circuits on first mismatch. Vulnerable to timing attacks that extract the API key one character at a time. The Telegram webhook endpoint correctly uses `CryptographicOperations.FixedTimeEquals`, showing awareness of the issue, but the main auth handler does not.

**Fix:**

```csharp
if (!CryptographicOperations.FixedTimeEquals(
    System.Text.Encoding.UTF8.GetBytes(providedKey),
    System.Text.Encoding.UTF8.GetBytes(Options.ApiKey)))
    return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
```

---

### S-5.1 PTY provides full unsandboxed shell access

**File:** `OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs:59-64`

The terminal endpoint creates a real PTY (`bash -i` on Linux, `cmd.exe` on Windows) with zero restriction on what can be executed. Unlike `ShellExecTool` (LLM-invoked, with timeout/truncation), this is a direct interactive shell for any authenticated client. A compromised API key gives full shell access to the host.

**Fix:** (a) Make terminal access opt-in via a separate config flag. (b) Require a separate elevated auth token distinct from the general API key. (c) Consider restricting the shell user on Linux.

---

### S-6.1 Config store key allows path traversal

**File:** `OpenAgent.ConfigStore.File/FileConfigStore.cs:25` (called from `AdminEndpoints.cs:106`)

The `key` from the URL route parameter is used in `Path.Combine(_directory, $"{key}.json")`. While the POST endpoint validates against registered configurable keys first (mitigating direct exploitation), if a provider ever registered a key containing path separators, the store would write outside the config directory.

**Fix:**

```csharp
if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
    throw new ArgumentException("Invalid config key");
```

---

## 2. LLM Provider Implementations

### L-5 Orphaned tool calls when ExecuteToolAsync throws

**File:** `AzureOpenAiTextProvider.cs:206` and `AnthropicSubscriptionTextProvider.cs:265`

If `agentLogic.ExecuteToolAsync` throws during a tool call round, the assistant message with tool calls was already persisted, but the tool result message is never added. This creates an orphaned tool call in the database. On the next request, `BuildChatMessages` detects and skips the orphaned round, but the conversation loses the entire tool call round from context. The voice session (`AzureOpenAiVoiceSession.cs:259-276`) already handles this correctly with try/catch.

**Fix:** Wrap `ExecuteToolAsync` in try/catch in both text providers:

```csharp
string result;
try
{
    result = await agentLogic.ExecuteToolAsync(conversationId, name, argsString, ct);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    logger.LogError(ex, "Tool {ToolName} failed for conversation {ConversationId}", name, conversationId);
    result = JsonSerializer.Serialize(new { error = ex.Message });
}
```

---

## 3. Channel Providers

### CH-7 Telegram webhook secret not persisted -- message loss on restart

**File:** `OpenAgent.Channel.Telegram/TelegramChannelProvider.cs:108`

The `webhookId` is persisted to connection config, but `webhookSecret` is NOT. When `_options.WebhookSecret` is null, a new GUID is generated on every `StartAsync`. Since `StopAsync` leaves the webhook registered, Telegram delivers queued updates between restarts carrying the OLD secret. The restarted instance has a NEW secret. The webhook endpoint rejects these with 401, causing message loss.

**Fix:** Persist the auto-generated webhook secret alongside the webhookId in the connection config on first generation.

---

### CH-14 WhatsApp dedup dictionary not thread-safe

**File:** `OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs:29, 184-222`

`_processedMessages` is a plain `Dictionary<string, DateTime>` with no synchronization. `HandleMessageAsync` is called from `Task.Run` in `HandleNodeEvent`, meaning multiple messages invoke `TryRecordMessage` concurrently on different thread-pool threads. `Dictionary<TKey, TValue>` is not thread-safe for concurrent reads and writes -- can corrupt internal state causing `ArgumentException`, infinite loops, or lost entries.

**Fix:** Replace with `ConcurrentDictionary<string, DateTime>`. Rework eviction logic to be thread-safe (lock around eviction block or accept slightly-over-threshold with `ConcurrentDictionary`).

---

## 4. Conversation Storage & Compaction

### ST-1 Compaction context as "system" role breaks Anthropic Messages API

**File:** `OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:353-361`

`GetMessages` prepends a synthetic `Message` with `Role = "system"` when compaction context exists. The Anthropic provider passes `msg.Role` through into the messages array. The Anthropic Messages API does not accept "system" as a role in the messages list -- system content must be in the top-level `system` parameter. This causes an API 400 error on any compacted conversation when using the Anthropic provider.

**Fix:** Either (a) change the context message to `Role = "user"` with a `[Conversation context summary]` prefix, or (b) have each provider's `BuildMessages` check for `Role == "system"` from the store and handle appropriately (Anthropic: append to system blocks; Azure: include as-is).

---

### ST-2 Compaction can split tool call / result pairs

**File:** `OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:432-440`

The compaction boundary uses `KeepLatestMessagePairs * 2`, assuming each "pair" is exactly 2 messages. Tool call rounds produce 3+ messages (assistant-with-tool-calls, tool results, final assistant). The boundary could land on an assistant message with tool calls while corresponding tool results fall in the "keep" window, producing orphaned tool results after compaction.

**Fix:** After computing the initial boundary, walk forward/backward to ensure it does not split a tool call round. If the last message in `toCompact` has `ToolCalls != null`, include all subsequent tool result messages. If the first kept message is a tool result (`Role == "tool"`), walk the boundary backward to include the full round.

---

## 5. Skills System

### SK-6 Full-row Update from skill tools overwrites concurrent changes

**File:** `OpenAgent.Skills/SkillToolHandler.cs:80` and `OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:244-281`

The `Update` method writes ALL conversation fields (compaction state, token counts, voice session, etc.). When `ActivateSkillTool` calls `store.Update(conversation)`, it writes back a stale snapshot of every other field. If compaction is running concurrently and updates `CompactedUpToRowId`/`Context`, the skill activation's `Update` overwrites those fields with stale values, undoing the compaction.

**Fix:** Add a targeted `UpdateActiveSkills` method to `IConversationStore`:

```csharp
void UpdateActiveSkills(string conversationId, List<string>? activeSkills);
```

SQL: `UPDATE Conversations SET ActiveSkills = @skills WHERE Id = @id`

---

## 6. Terminal & Scheduled Tasks

### TT-2.1 Terminal sessions never cleaned up on WebSocket disconnect

**File:** `OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs:96-109`

When the WebSocket disconnects, the `finally` block only removes the bridge CTS from `ActiveBridges` and closes the WebSocket. It does NOT call `sessionManager.CloseAsync(sessionId)`. The terminal session (with its live bash process) remains in `TerminalSessionManager._sessions` indefinitely. If the user never reconnects, the bash process runs forever until the `MaxSessions` limit (4) is hit, blocking all new sessions.

**Fix:** (a) Add a configurable idle timeout to `TerminalSessionManager` that auto-closes sessions after N minutes of no WebSocket connection, or (b) add an explicit "close session" endpoint the frontend calls on tab close.

---

### TT-4.3 intervalMs validation works by coincidence of C# nullable semantics

**File:** `OpenAgent.ScheduledTasks/ScheduleCalculator.cs:71`

`if (schedule.IntervalMs is <= 0)` runs unconditionally in `ValidateIndividual`. When the caller set `cron` (not `intervalMs`), `schedule.IntervalMs` is `null`. The pattern `null is <= 0` evaluates to `false` in C#, so it does not trigger the error. The code is correct by accident. Additionally, there is no minimum interval guard -- `intervalMs: 1` (1ms) would fire on every 30-second tick.

**Fix:** Guard with explicit null check and add minimum:

```csharp
if (schedule.IntervalMs is not null && schedule.IntervalMs.Value <= 0)
    return "intervalMs must be a positive number.";
if (schedule.IntervalMs is not null && schedule.IntervalMs.Value < 60000)
    return "intervalMs must be at least 60000 (1 minute).";
```

---

## 7. API Endpoints

### EP-4.1 Unguarded DateTimeOffset.Parse on user input

**File:** `OpenAgent.Api/Endpoints/LogEndpoints.cs:82-83`

`since` and `until` query parameters are parsed with `DateTimeOffset.Parse(since)` which throws `FormatException` on invalid input, surfacing as 500 instead of 400.

**Fix:** Use `DateTimeOffset.TryParse` and return `Results.BadRequest(new { error = "Invalid 'since' format" })` on failure.

---

### EP-4.4 Unbounded file read in FileExplorer

**File:** `OpenAgent.Api/Endpoints/FileExplorerEndpoints.cs:80-82`

`reader.ReadToEnd()` reads entire file content into a string with no size check. A multi-gigabyte file (SQLite database, uploaded binary) causes OOM.

**Fix:** Check `new FileInfo(fullPath).Length` against a cap (e.g., 10MB) before reading, return 413 Payload Too Large if exceeded.

---

### EP-5.1 ActiveBridges race condition in terminal endpoint

**File:** `OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs:98`

The `finally` block does `ActiveBridges.TryRemove(sessionId, out _)` unconditionally. If connection B arrived and registered itself between lines 79 and 98, A's finally block removes B's CTS. B then runs without eviction protection -- a third connection C won't know to cancel B.

**Fix:** Use `TryRemove` with value equality check: only remove if the stored CTS is the one this connection registered. Or use `ConcurrentDictionary`'s `ICollection<KVP>.Remove` overload.

---

## 8. Frontend

### FE-6 No response status checking in settings API

**File:** `src/web/src/apps/settings/api.ts` (10 instances)

Every function calls `res.json()` without checking `res.ok`. A 401, 500, or 404 returns an HTML error page or JSON error body. `res.json()` either throws (on HTML) or silently returns a non-matching shape that corrupts caller state.

**Fix:**

```typescript
async function checkedJson<T>(res: Response): Promise<T> {
  if (!res.ok) throw new Error(`API error: ${res.status}`);
  return res.json();
}
```

Replace all `return res.json()` with `return checkedJson(res)`.

---

## 9. Coupling & Contracts

### CO-1 ScheduledTaskService concrete type crosses project boundary

**File:** `OpenAgent.Api/Endpoints/ScheduledTaskEndpoints.cs:23`

`ScheduledTaskEndpoints` resolves `ScheduledTaskService` (concrete class from `OpenAgent.ScheduledTasks`) directly in every endpoint handler. Forces `OpenAgent.Api.csproj` to take a direct project reference. Violates the architecture rule: "extract an interface into OpenAgent.Contracts."

**Fix:** Extract `IScheduledTaskService` into `OpenAgent.Contracts`:

```csharp
public interface IScheduledTaskService
{
    Task AddAsync(ScheduledTask task, CancellationToken ct);
    Task UpdateAsync(string taskId, Action<ScheduledTask> patch, CancellationToken ct);
    Task RemoveAsync(string taskId, CancellationToken ct);
    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct);
    Task<ScheduledTask?> GetAsync(string taskId, CancellationToken ct);
    Task RunNowAsync(string taskId, string? promptOverride, CancellationToken ct);
}
```

---

## Summary

| Domain | Count | IDs |
|--------|-------|-----|
| Security & Trust Boundary | 4 | S-3.1, S-4.1, S-5.1, S-6.1 |
| LLM Providers | 1 | L-5 |
| Channel Providers | 2 | CH-7, CH-14 |
| Storage & Compaction | 2 | ST-1, ST-2 |
| Skills System | 1 | SK-6 |
| Terminal & Tasks | 2 | TT-2.1, TT-4.3 |
| API Endpoints | 3 | EP-4.1, EP-4.4, EP-5.1 |
| Frontend | 1 | FE-6 |
| Coupling & Contracts | 1 | CO-1 |

### Top 10 Fixes by Impact

| Priority | Finding | Effort |
|----------|---------|--------|
| 1 | Constant-time API key comparison (S-4.1) | 1 line |
| 2 | Trailing separator on `StartsWith` path checks (S-1.1, S-2.1 -- medium severity, but blocks path traversal) | Small |
| 3 | `using` on `HttpResponseMessage` in both text providers (L-2 -- medium severity, but trivial fix) | 4 lines |
| 4 | Wrap `ExecuteToolAsync` in try/catch in text providers (L-5) | Small |
| 5 | TOCTOU DNS rebinding -- `ConnectCallback` on SocketsHttpHandler (S-3.1) | Medium |
| 6 | Targeted `UpdateActiveSkills` method (SK-6) | Medium |
| 7 | Compaction boundary -- don't split tool call/result pairs (ST-2) | Medium |
| 8 | Persist Telegram webhook secret (CH-7) | Small |
| 9 | `res.ok` checks in frontend settings API (FE-6) | Small |
| 10 | `DateTimeOffset.TryParse` + file size guard in endpoints (EP-4.1, EP-4.4) | Small |
