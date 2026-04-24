# Tools & Skills Review — 2026-04-23

## Summary

The tool and skill layer has a clean `ITool`/`IToolHandler` contract, well-tested skill discovery and frontmatter parsing, and a disciplined scheduler with separated execution, delivery, and persistence. However there are several high-severity correctness bugs: the FileSystem tool path-gate is a raw `StartsWith` prefix match with no trailing separator (siblings of `dataPath` pass the gate), the Skill resource loader has the same pattern, `ExpandTool` fetches messages by ID ignoring the calling `conversationId` (cross-conversation read), there is no tool-name dedup in `AgentLogic._allTools` (last-wins with silent shadowing), concurrent `activate_skill` / `set_intention` / `set_model` calls race on the store's `Update` (blind overwrite), `WebFetchToolHandler` builds an `HttpClient` with default redirect-following so the SSRF validation is bypassed by a 302, and `ScheduleCalculator` always parses cron with 5-field `CronFormat.Standard` while the tool schema advertises "5 or 6 fields" (6-field throws). `SetIntentionTool` description says 500-char max, code enforces 1000. Scheduled tasks: missed-run replay blocks `StartAsync` (2s stagger per task), and the same task can be re-selected while in-flight because `NextRunAt` isn't cleared at tick time. Pluses: skill resource loader correctly blocks `../` traversal, `UrlValidator` has strong RFC1918/link-local/ULA coverage, the scheduler runs LLM work outside the lock, and skill activation persists on `Conversation.ActiveSkills` so compaction can't drop it.

## Strengths

- **`ITool.ExecuteAsync(arguments, conversationId, ct)` passes conversation context through**, enabling conversation-mutating tools (skills, intention, model switch) to work cleanly (src/agent/OpenAgent.Contracts/ITool.cs:12).
- **Skill persistence via `Conversation.ActiveSkills`** — activation survives compaction; `SystemPromptBuilder.Build` appends bodies fresh each turn (src/agent/OpenAgent/SystemPromptBuilder.cs:110-136). Spec-compliant progressive disclosure: catalog always, body when active, resources on demand.
- **`SkillCatalog.ReadSkillBody`** re-reads from disk so SKILL.md edits picked up mid-conversation (src/agent/OpenAgent.Skills/SkillCatalog.cs:66-82). The `reload_skills` tool lets the agent refresh without restart.
- **`SkillFrontmatterParser`** is lenient in the right places — handles colons inside description values, accepts optional `metadata:` nested block, robust to CRLF (src/agent/OpenAgent.Skills/SkillFrontmatterParser.cs:28, 102-109). Well-tested.
- **`ScheduledTaskService` lock discipline** — LLM execution and delivery run *outside* the `SemaphoreSlim`; state updates re-enter the lock (src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs:272-298). Prevents one slow task from starving CRUD.
- **`DeliveryRouter` swallows delivery failures** so transient channel errors don't mark the task run as failed — the LLM completion succeeded (src/agent/OpenAgent.ScheduledTasks/DeliveryRouter.cs:64-77).
- **`CreateScheduledTaskTool` always binds to the caller's `conversationId`** — no way to inject a task into another conversation (src/agent/OpenAgent.ScheduledTasks/ScheduledTaskToolHandler.cs:88-98).
- **`ActivateSkillResourceTool` explicitly rejects `../`** (src/agent/OpenAgent.Skills/SkillToolHandler.cs:248-250) and size-limits to 256 KB. The `SkillToolHandlerTests.ActivateSkillResource_blocks_path_traversal` regression test covers it.
- **`UrlValidator.IsPrivateOrReserved`** covers IPv6 loopback, link-local (fe80::/10), ULA (fc00::/7), IPv4-mapped IPv6, RFC1918, CGN (100.64/10), benchmark (198.18/15), 169.254 AWS metadata (src/agent/OpenAgent.Tools.WebFetch/UrlValidator.cs:79-128).
- **`FileEditTool` requires unique `old_text` match** and refuses zero-op edits (src/agent/OpenAgent.Tools.FileSystem/FileEditTool.cs:63-74) — avoids the classic "edit applied to wrong place" LLM tool failure.

## Bugs

### Path prefix check accepts sibling directories (severity: high)
- **Files:** `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs:39`, `FileWriteTool.cs:40`, `FileAppendTool.cs:39`, `FileEditTool.cs:43`, plus `src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs:73` and `src/agent/OpenAgent.Skills/SkillToolHandler.cs:249`.
- **Issue:** Each uses `fullPath.StartsWith(basePath, OrdinalIgnoreCase)`. `basePath` comes from `Path.GetFullPath(environment.DataPath)` (no trailing slash). If `basePath == "C:\data\agent"` and the attacker passes `../agent-evil/x.txt`, `GetFullPath` returns `C:\data\agent-evil\x.txt`, which starts with `C:\data\agent` → passes. Same applies to `skillDir` for the skill resource tool, and to `workspacePath` for the shell `cwd` escape check.
- **Fix:** Compute once at handler construction: `basePath = Path.GetFullPath(environment.DataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;` and use `fullPath == basePath[..^1] || fullPath.StartsWith(basePath, OrdinalIgnoreCase)`. Or use `Path.GetRelativePath` and reject results starting with `..`.

### ExpandTool ignores calling conversationId (severity: high)
- **Files:** `src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs:38-44`, `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:372`.
- **Issue:** `ExpandTool.ExecuteAsync(arguments, conversationId, ct)` discards `conversationId` and calls `store.GetMessagesByIds(messageIds)` directly. The SQLite query (I looked; it filters only by `Id IN (…)`) returns any matching message across all conversations. Message IDs are GUIDs so not trivially brute-forceable, but if the agent sees a `[ref: …]` annotation from compaction belonging to another conversation, or leaks via logs, they'd be accessible.
- **Fix:** Extend the store signature to `GetMessagesByIds(string conversationId, IReadOnlyList<string> ids)` and filter by both in SQL. Pass `conversationId` through in `ExpandTool`.

### No tool-name uniqueness check in AgentLogic (severity: high)
- **File:** `src/agent/OpenAgent/AgentLogic.cs:18,30`.
- **Issue:** `_allTools = toolHandlers.SelectMany(h => h.Tools).ToList()` — no dedup. `ExecuteToolAsync` does `_allTools.FirstOrDefault(t => t.Definition.Name == name)`. If two handlers register the same tool name, the first wins silently, and the LLM sees the definition from whichever the `Tools` list enumerator hit first (via `IAgentLogic.Tools = _allTools.Select(t => t.Definition).ToList()` — the LLM gets both listed!). So two tool entries with the same name go to the LLM and one of them never runs.
- **Fix:** Throw at startup on duplicates: `var dup = _allTools.GroupBy(t => t.Definition.Name).FirstOrDefault(g => g.Count() > 1); if (dup != null) throw new InvalidOperationException($"Duplicate tool name: {dup.Key}");`. Also consider a case-insensitive check since some LLM providers lowercase tool names.

### Concurrent activate_skill / set_intention / set_model race on Update (severity: high)
- **Files:** `src/agent/OpenAgent.Skills/SkillToolHandler.cs:58-81,115-130`; `src/agent/OpenAgent.Tools.Conversation/SetIntentionTool.cs:41-47`, `SetModelTool.cs:58-67`, `ClearIntentionTool.cs:29-34`.
- **Issue:** Classic read-modify-write. Two concurrent `activate_skill` calls both `store.Get(conversationId)`, append to their local copy, and each write-back with `store.Update(conversation)`. The second wins; the first activation is silently lost. Same applies to deactivate, intention, and model. The `max_active_skills = 5` check (SkillToolHandler.cs:74) also races — 4→6 is reachable by two parallel activations.
- **Risk:** LLMs (especially Anthropic) can issue multiple tool calls in a single turn, and providers may execute them in parallel.
- **Fix:** Serialize per-conversation mutations with a `ConcurrentDictionary<string, SemaphoreSlim>` at the tool layer, or move the merge into the store (e.g. SQLite `json_set` under a transaction). Simplest is option A.

### WebFetch follows redirects with no SSRF re-validation (severity: high)
- **Files:** `src/agent/OpenAgent.Tools.WebFetch/WebFetchToolHandler.cs:14`, `WebFetchTool.cs:63`.
- **Issue:** `new HttpClient { Timeout = TimeSpan.FromSeconds(30) }` — `HttpClientHandler.AllowAutoRedirect` defaults to `true`. `UrlValidator.ValidateWithDnsAsync` runs on the user-supplied URL only. A malicious public URL `https://attacker.com/redirect` returning `Location: http://169.254.169.254/latest/meta-data/iam/security-credentials/` is followed silently — full SSRF bypass against AWS metadata, internal admin panels, Docker socket over TCP, etc.
- **Fix:** Build a `SocketsHttpHandler { AllowAutoRedirect = false }`, handle redirects manually in `WebFetchTool`, and run `ValidateWithDnsAsync` on each `Location` before issuing the next request. Cap at 5 redirects. Alternatively (covers DNS rebinding too): set `SocketsHttpHandler.ConnectCallback` and validate the resolved IP just-in-time before the TCP connect.

### ScheduleCalculator parses cron 5-field only but schema says "5 or 6 fields" (severity: medium)
- **Files:** `src/agent/OpenAgent.ScheduledTasks/ScheduleCalculator.cs:23,67`; `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskToolHandler.cs:61`.
- **Issue:** `CronExpression.Parse(schedule.Cron)` defaults to `CronFormat.Standard` (5 fields). Cronos supports 6-field via `CronFormat.IncludeSeconds` but that flag isn't passed. The create-task tool description says `Cron expression (5 or 6 fields)` — a 6-field string throws `CronFormatException` at `Validate` time with a confusing "expected X fields" message.
- **Fix:** `var format = schedule.Cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;` and pass it to both `Parse` sites.

### SetIntentionTool description says "500 chars" but enforces 1000 (severity: low)
- **File:** `src/agent/OpenAgent.Tools.Conversation/SetIntentionTool.cs:13,24,38`.
- **Issue:** Constant `MaxIntentionLength = 1000`, but the parameter description reads `Keep under 500 characters.` The LLM sees 500 in the schema; the server enforces 1000. Minor, but misleading.
- **Fix:** Align the two — pick one limit.

### Scheduled-task tick can re-select in-flight task (severity: medium)
- **File:** `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs:229-263,268-298`.
- **Issue:** The tick selects `tasks where Enabled && NextRunAt <= now`. `NextRunAt` is only advanced in `ExecuteTaskAsync` *after* LLM completion and delivery finish, under the lock. If the run takes > 30s (typical for LLM), the next tick re-selects the same task because its `NextRunAt` is still the past value.
- **Fix:** At tick time, clear `NextRunAt` (or set a sentinel `RunningAt` field) under the lock before dispatching. Restore after completion.

### Missed-task replay blocks StartAsync with sequential 2s delays (severity: medium)
- **File:** `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs:324-342`.
- **Issue:** `RunMissedTasksAsync` is `await`ed in `StartAsync` before `Timer` arming. Each missed task runs sequentially with `Task.Delay(2s)`. If 30 tasks are overdue after a 1h outage, `StartAsync` blocks for > 60s — during which the hosted-service pipeline (and thus the whole host) is held up. It also skips subsequent tasks if one throws before the delay.
- **Fix:** Run replay fire-and-forget (`_ = Task.Run(RunMissedTasksAsync, ct)`) and arm the timer immediately. Cap missed replays to N (e.g. 3) per task so a week-long outage doesn't fire 10k catch-ups. Also: `RunMissedTasksAsync` uses a passed `ct` — when `cancellationToken` is already cancelled (shutdown mid-startup) it will throw on the first `Task.Delay`, fine, but hostability is questionable.

### Cron missed-run on long outage fires once then jumps ahead (severity: low)
- **File:** `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs:324-342` + `ScheduleCalculator.ComputeNextRun`.
- **Issue:** A task scheduled `* * * * *` (every minute) after a 10-hour outage has exactly one `NextRunAt` still in the past. `RunMissedTasksAsync` runs once, then `ExecuteTaskAsync` recomputes `NextRunAt` for "now" → next minute. The 600 missed occurrences are dropped. Arguably the right behavior — but undocumented and surprising if the user expected catch-up.
- **Fix:** Document the "one catch-up per schedule on restart" semantic. Optional: replay up to K missed occurrences via `GetOccurrences`.

### Scheduled task `required` includes `description` and `deleteAfterRun` but handler treats them optional (severity: low)
- **File:** `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskToolHandler.cs:70,85-86`.
- **Issue:** `required = new[] { "name", "prompt", "schedule", "description", "deleteAfterRun" }` — but the code uses `TryGetProperty` for both. The LLM is told they're mandatory; providers like Azure OpenAI enforce this in the tool schema and reject calls missing them.
- **Fix:** Drop `description` and `deleteAfterRun` from `required`. Only `name`, `prompt`, `schedule` are truly needed.

### Scheduled task trigger endpoint has no body size limit or content-type check (severity: medium)
- **File:** `src/agent/OpenAgent.Api/Endpoints/ScheduledTaskEndpoints.cs:97-116`.
- **Issue:** `reader.ReadToEndAsync` reads the full body (Kestrel default cap 30 MB) and injects verbatim into the prompt. Also no content-type filter: `Content-Type: application/octet-stream` with binary data goes straight into the LLM.
- **Risk:** A large webhook body becomes a big token bill; replayed requests re-fire expensive LLM calls.
- **Fix:** Cap via `if (request.ContentLength > 64_000) return Results.StatusCode(413);` plus an explicit enforceable max on `promptOverride` length. Add idempotency key support if desired. (Auth: the `/trigger` endpoint is gated by `RequireAuthorization()` via `MapGroup("/api/scheduled-tasks")` at line 20, which the integration test relies on — so not an auth bug.)

### FileEditTool diff generator splits on '\n' only (severity: low)
- **File:** `src/agent/OpenAgent.Tools.FileSystem/FileEditTool.cs:97-98`.
- **Issue:** `oldContent.Split('\n')` preserves a trailing `\r` on every line of a CRLF file. The diff display is `- 12: foo\r` with a literal `\r`. Not corrupting the file (file content is untouched — `WriteAllTextAsync` just writes the post-replace string as-is, so CRLF is preserved if `old_text`/`new_text` used CRLF), but the diff output is wonky and line counts may desync.
- **Fix:** `.Split('\n').Select(l => l.TrimEnd('\r')).ToArray()` for display only.

### FileReadTool drops line terminators so subsequent file_edit may not match CRLF files (severity: low)
- **File:** `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs:64-74`.
- **Issue:** `File.ReadAllLinesAsync` strips both `\r\n` and `\n` so the agent's view of the file has LF-only separators (which aren't even present in the returned string — lines are joined with `\n` on line 74). When the agent later composes `old_text` by copying contiguous lines, the text passed to `file_edit` uses `\n` between lines, but the on-disk file has `\r\n`. `FileEditTool.IndexOf(oldText, Ordinal)` then returns -1 and the edit fails with "old_text not found in file".
- **Fix:** Either read+return with original line endings (`File.ReadAllBytes` + decode, preserve newlines), or document in `file_read` description that line endings are normalized to LF and `file_edit` automatically handles CRLF. Cleanest: `FileEditTool` could normalize both sides' line endings before `IndexOf`, then re-insert the original terminators when writing back.

### WebFetch byte vs char confusion (severity: low)
- **File:** `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs:13,74-76`.
- **Issue:** `MaxResponseBytes = 2_000_000` used as `html[..MaxResponseBytes]` — but `html` is a UTF-16 string so this is 2M *chars* (up to 4 MB bytes). Name is misleading.
- **Fix:** Rename to `MaxResponseChars` or read bytes directly and decode a safe prefix.

## Smells

### Path-validation duplicated across four file tools (severity: medium)
- **Files:** `FileReadTool.cs:37-46`, `FileWriteTool.cs:38-47`, `FileAppendTool.cs:37-46`, `FileEditTool.cs:41-50`.
- **Issue:** The identical 10-line resolve-and-check block is pasted four times, including error message with `SymlinkRoots.List`. Any fix (like the prefix-boundary bug) has to be applied four times.
- **Suggestion:** Extract `PathGate.Resolve(basePath, userPath) → (string? fullPath, string? errorJson)` in `OpenAgent.Tools.FileSystem` and call from each tool.

### Tool `Parameters` are anonymous objects (severity: low)
- **Files:** Every `ITool.Definition.Parameters = new { type = "object", … }`.
- **Issue:** Anonymous types serialize fine today with reflection-based STJ, but the CLAUDE.md conventions say *"never anonymous types for API payloads"*. In a trimmed/AOT publish they trigger IL trimming warnings. Definitions are re-allocated on every `ExecuteAsync` call for some (not FileEditTool — cached as `{ get; }`, good; but others use `{ get; } = …` so actually all are cached — OK on that).
- **Suggestion:** Lowest-effort win is a shared helper `ToolParams.Object(params (string name, object schema, bool required, string description)[] props)` that builds a `Dictionary<string, object>`.

### Tool descriptions read as reference, not decision prompts (severity: medium)
- **Files:** `WebFetchTool.cs:18`, `FileEditTool.cs:17`, `FileReadTool.cs:17`, `ExpandToolHandler.cs:21`, `SetIntentionTool.cs:18`.
- **Issue:** CLAUDE.md explicitly notes *"Tool descriptions prescribe when to call, not just what the tool does"*, pointing to `SearchMemoryTool` as the model. `web_fetch` says "Fetch a URL and extract readable content as markdown" — no guidance on when the LLM should call it versus skip. `file_read` same. Scheduled-task tools partially do this ("If the task involves reading or writing files, ask the user which directory to use").
- **Suggestion:** Each tool description should include a "Call this when…" paragraph up front.

### Error shapes inconsistent across tools (severity: low)
- **Files:** Everywhere — `{ error = "…" }` vs `{ success = false, error = "…" }` (WebFetch) vs `{ error, path, code? }`.
- **Issue:** `WebFetchTool` returns `{ "success": false, … }` but `FileReadTool` returns `{ "error": "…" }`. No shared shape. Makes the LLM's life harder when building fallback logic.
- **Suggestion:** Define `ToolResult.Error(code, message, hints?)` and `ToolResult.Ok(payload)` helpers in `OpenAgent.Contracts`.

### Tool definitions not cached across handlers / lookup is linear (severity: low)
- **File:** `src/agent/OpenAgent/AgentLogic.cs:24,30`.
- **Issue:** `Tools => _allTools.Select(t => t.Definition).ToList()` re-allocates a `List<AgentToolDefinition>` on every access (called once per LLM turn). `ExecuteToolAsync` does `FirstOrDefault` (O(N)) on the tool list. With ~15+ tools this is trivial, but worth moving to a `Dictionary<string, ITool>` since tools are fixed at DI time.
- **Suggestion:** `private readonly IReadOnlyList<AgentToolDefinition> _definitions = _allTools.Select(t => t.Definition).ToList();` and `_toolMap = _allTools.ToDictionary(t => t.Definition.Name);`.

### `ShellExecTool.BuildDescription` runs at type-init (severity: low)
- **File:** `src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs:15-53`.
- **Issue:** `Definition { get; } = new() { … Description = BuildDescription() }` runs synchronous `File.Exists` probes for Git Bash at instance construction. If the host is probed before Git install completes (edge case), the cached description is stale.
- **Suggestion:** Compute at handler construction and pass into `ShellExecTool` constructor.

### Shell output may truncate on fast exits (severity: medium)
- **File:** `src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs:125-155`.
- **Issue:** `BeginOutputReadLine` posts `OutputDataReceived` events asynchronously. `WaitForExitAsync` returns when the process exits, but trailing `.Data` events for the last stdout bytes may still be in flight. The snapshot at line 155 may miss trailing lines. .NET docs recommend a follow-up synchronous `WaitForExit()` to drain pipes.
- **Suggestion:** After `WaitForExitAsync`, call `process.WaitForExit()` (no-op if already exited, but drains the async readers) before reading `outputChunks`.

### Shell has no unit test coverage (severity: medium)
- **File:** `src/agent/OpenAgent.Tests/` — no `ShellExecToolTests.cs`.
- **Issue:** The most dangerous tool (arbitrary command exec) has zero direct test coverage. Happy path, timeout behavior, cwd-escape rejection, cancellation, tail truncation — all untested.
- **Suggestion:** Add a minimal `ShellExecToolTests.cs` with at least happy path, timeout, and cwd escape tests (Linux + Windows conditional).

### Magic limits scattered (severity: low)
- **Files:** `SkillToolHandler.cs:256` (256_000), `SkillDiscovery.cs:18` (256_000), `FileReadTool.cs:12` (1_048_576), `ShellExecTool.cs:13` (2000 / 50*1024).
- **Suggestion:** `ToolLimits` static class.

### `SkillCatalog.GetSkillResources` uses `SearchOption.AllDirectories` unbounded (severity: low)
- **File:** `src/agent/OpenAgent.Skills/SkillCatalog.cs:103`.
- **Issue:** Recursively lists every file in `scripts/`, `references/`, `assets/`. `maxResources = 50` caps output but the enumeration walks the whole tree anyway. If a skill has a `node_modules` under `references/`, it's walked. Also follows symlinks by default.
- **Suggestion:** Use `EnumerateFiles` and stop at cap; add the same `IgnoredDirectories` filter `SkillDiscovery` uses.

### Silent `Catch` in `ScheduledTaskStore` (severity: low)
- **File:** `src/agent/OpenAgent.ScheduledTasks/Storage/ScheduledTaskStore.cs:54-63,80-83`.
- **Issue:** `catch (IOException)` and `catch (JsonException)` return without logging. A corrupt `scheduled-tasks.json` (e.g. crash during Save) silently becomes an empty list and next Save overwrites it. No log, no backup, no user notice.
- **Suggestion:** Log at Warning level with path and exception message. Optional: rename corrupt file to `.corrupt-{timestamp}` before starting fresh.

### `ExpandTool` tool description relies on compaction ref notation (severity: low)
- **File:** `src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs:21`.
- **Issue:** "when the conversation context summary references messages you need to see in full" — assumes compaction is on and emits `[ref: …]` tags. If compaction isn't configured or the summarizer doesn't emit refs, the tool's description is misleading.
- **Suggestion:** Document the contract explicitly or make the tool no-op (not registered) when compaction is off.

### `FileReadTool` returns `total_lines` before filtering (severity: low)
- **File:** `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs:65,79`.
- **Issue:** `total_lines` is the count of all lines in the file, which is correct. But the hint `… ({remaining} more lines)` appends into `content` string rather than being a separate JSON field — harder for the LLM to parse programmatically.
- **Suggestion:** Return `{ path, total_lines, offset, returned_lines, remaining_lines, content }`.

## Open Questions

1. **Should `/api/tools/{toolName}/execute` tools that mutate conversations be gated differently?** `ToolEndpoints.cs:62-66` creates a throwaway `tool-test-{guid}` conversation ID. Calling `activate_skill` against this creates a skill activation on a conversation that won't exist in the store — the tool returns "Conversation not found". That's defensive, but surprising. Should we categorize tools into "pure" (OK to call from this endpoint) and "conversation-mutating" (blocked) at the tool level?
2. **Does `ScheduledTaskService.StopAsync` drain in-flight task executions?** It calls `_timer.Change(Timeout.Infinite)` to stop new ticks, but active `ExecuteTaskAsync` is running with `CancellationToken.None`. A shutdown during an LLM call waits for the provider's timeout. Should we thread `cancellationToken` into the execution path?
3. **Is `max_active_skills = 5` the right cap?** Hard-coded in `SkillToolHandler.cs:65`. A user with many fine-grained skills may hit this. Configurable via AgentConfig?
4. **Scheduler timezone vs system prompt's hardcoded Europe/Copenhagen?** `SystemPromptBuilder.Build` always injects Europe/Copenhagen. Cron tasks default to UTC. A user in Copenhagen scheduling "every weekday at 9am" without specifying timezone gets UTC (so fires at 10am or 11am locally). The create-task tool description says "IANA timezone for cron (default UTC)" — clear, but easy to miss. Should scheduler default match prompt's displayed timezone?
5. **Skill resource loader reads UTF-8 text only.** `File.ReadAllText(fullPath)` without encoding argument uses UTF-8 + BOM detection. What if a skill ships a binary `.zip` or `.png` under `assets/`? The tool returns a string which may contain invalid UTF-16 surrogates, and the LLM sees garbage. Should the tool base64-encode binary results or refuse non-text extensions?
6. **`SetIntentionTool` lets agent overwrite user-set intention silently.** Should the tool distinguish user-set vs agent-set (e.g. boolean `Conversation.IntentionSetBy`)?
7. **`reload_skills` tool is surprisingly powerful.** Any active conversation can globally reload the skill catalog. For multi-user deployments this is weird — one user's reload affects everyone. Should it be admin-only or auto-reload via `FileSystemWatcher`?

## Files reviewed

Tool handlers (read in full):
- `src/agent/OpenAgent.Tools.FileSystem/{FileSystemToolHandler,FileReadTool,FileWriteTool,FileAppendTool,FileEditTool,SymlinkRoots}.cs`
- `src/agent/OpenAgent.Tools.Shell/{ShellToolHandler,ShellExecTool}.cs`
- `src/agent/OpenAgent.Tools.WebFetch/{WebFetchToolHandler,WebFetchTool,UrlValidator,ContentExtractor,IDnsResolver}.cs`
- `src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs`
- `src/agent/OpenAgent.Tools.Conversation/{ConversationToolHandler,GetAvailableModelsTool,GetCurrentModelTool,SetModelTool,SetIntentionTool,ClearIntentionTool}.cs`

Skills (read in full):
- `src/agent/OpenAgent.Skills/{SkillDiscovery,SkillCatalog,SkillFrontmatterParser,SkillEntry,SkillToolHandler}.cs`
- `src/agent/OpenAgent/{SystemPromptBuilder,AgentLogic}.cs`

Scheduled tasks (read in full):
- `src/agent/OpenAgent.ScheduledTasks/{ScheduleCalculator,ScheduledTaskExecutor,ScheduledTaskService,ScheduledTaskToolHandler,DeliveryRouter,ServiceCollectionExtensions}.cs`
- `src/agent/OpenAgent.ScheduledTasks/Models/ScheduledTask.cs`
- `src/agent/OpenAgent.ScheduledTasks/Storage/ScheduledTaskStore.cs`

Endpoints and contracts:
- `src/agent/OpenAgent.Api/Endpoints/{ToolEndpoints,ScheduledTaskEndpoints,ChatEndpoints}.cs`
- `src/agent/OpenAgent.Contracts/{ITool,IToolHandler,IAgentLogic,IConfigurable,ILlmTextProvider}.cs`
- `src/agent/OpenAgent/Program.cs` (endpoint-mapping section)

Tests (read in full):
- `src/agent/OpenAgent.Tests/{ExpandToolTests,FileSystemErrorMessageTests,ScheduledTaskEndpointTests}.cs`
- `src/agent/OpenAgent.Tests/Skills/{SkillFrontmatterParserTests,SkillDiscoveryTests,SkillCatalogTests,SkillToolHandlerTests,SkillIntegrationTests}.cs`
- `src/agent/OpenAgent.Tests/ConversationTools/{FakeModelProvider,GetAvailableModelsToolTests,GetCurrentModelToolTests,SetModelToolTests}.cs`
- `src/agent/OpenAgent.Tests/WebFetch/{WebFetchToolTests,ContentExtractorTests,UrlValidatorTests}.cs`
- `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`

Supporting (spot-checked):
- `src/agent/OpenAgent.Models/Conversations/Conversation.cs` (ActiveSkills + Intention fields)
- `src/agent/OpenAgent.Models/Configs/AgentConfig.cs`
- `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs` (GetMessagesByIds query)
