# Architecture & Core Review — 2026-04-23

## TL;DR

Core architecture is mostly sound: `CompletionEvent` is truly universal across REST and WebSocket,
provider pattern is applied consistently with keyed DI + `Func<string, T>` resolvers, and
`OpenAgent.Contracts` is a clean interface seam. But several invariants are silently bent. `SystemPromptBuilder` is a process-wide singleton whose internal `Dictionary` is rebuilt on an unsynchronised admin endpoint while request threads read it; `SqliteConversationStore.TryStartCompaction` fires `Task.Run` with `CancellationToken.None` on a hot path; `ConnectionManager.StartConnectionAsync` has a classic check-then-act race; `WebSocketRegistry.Register` silently evicts a concurrent client; and the WWWROOT extractor deletes and rewrites the static folder on every boot without a lock. The endpoint layer is thin with a few violations (file explorer/log endpoints carry real logic, admin does config merging), and two endpoints bypass the `Func<string, ILlmTextProvider>` resolver by calling `GetRequiredKeyedService` directly, diluting the documented pattern.

## Strengths

- **Universal `CompletionEvent` stream.** One abstract record hierarchy
  (`OpenAgent.Models/Common/CompletionEvent.cs:7-28`) is consumed identically by REST
  (`ChatEndpoints.cs:55-64`), text WebSocket (`WebSocketTextEndpoints.cs:107-131`), Telegram
  streaming/batch paths (`TelegramMessageHandler.cs:267-297, 462-488`), and scheduled tasks
  (`ScheduledTaskExecutor.cs:82-87`). This is the invariant most worth protecting.
- **Contracts segregation is real.** `OpenAgent.Contracts/` only depends on `OpenAgent.Models/`.
  Cross-project coupling goes through small interfaces (`IVoiceSessionManager`,
  `IConnectionManager`, `IWebSocketRegistry`, `IChannelProviderFactory`, `IOutboundSender`).
- **Provider pattern + keyed DI is consistent.** `Program.cs:167-175` registers providers as
  keyed singletons and exposes `Func<string, ILlmTextProvider>` / `Func<string, ILlmVoiceProvider>`
  resolvers. `VoiceSessionManager.cs:35`, `CompactionSummarizer.cs:58`,
  `ScheduledTaskExecutor.cs:79`, and `TelegramMessageHandler.cs:133` all use the resolver per
  message, so provider changes take effect without restart — exactly as advertised.
- **`IAgentLogic` is an injected collaborator, not an orchestrator.** Providers call
  `agentLogic.AddMessage`, `GetMessages`, `GetSystemPrompt`, and `ExecuteToolAsync` themselves
  (`AzureOpenAiTextProvider.cs:74, 185, 206, 234`). `AgentLogic.cs:18-45` contains no
  completion loop — just aggregation + delegation.
- **Tool discovery via `IToolHandler` aggregation works.** `AgentLogic.cs:18` flattens
  `IEnumerable<IToolHandler>` registrations, so adding a tool project is a single
  `builder.Services.AddSingleton<IToolHandler, ...>` line.
- **Channel setup metadata is data, not code.** `IChannelProviderFactory.ConfigFields /
  SetupStep` let the frontend build dynamic forms. `ConnectionEndpoints.cs:23-32` is one
  projection, no hardcoded channel switching anywhere in the UI path.

## Bugs

### SystemPromptBuilder mutates its cache without synchronisation (severity: high)
- **Location:** `src/agent/OpenAgent/SystemPromptBuilder.cs:21, 46-49, 60-89`
- **Issue:** `SystemPromptBuilder` is a singleton (`Program.cs:121`). It holds
  `private readonly Dictionary<string,string> _files` and exposes `Reload()` which
  calls `_files.Clear()` and repopulates from disk. `SystemPromptEndpoints.cs:47, 68` invoke
  `Reload()` from HTTP request threads while `Build()` is being called concurrently on
  every inbound text/voice/scheduled-task turn. `Dictionary<TKey,TValue>` is explicitly
  not thread-safe; concurrent mutation + read can corrupt its internal buckets and throw
  `InvalidOperationException` or return torn values.
- **Risk:** Intermittent exceptions or silently wrong system prompts whenever an admin
  saves or reloads prompt files while a conversation is in flight. Hard to reproduce,
  easy to misdiagnose as "flaky LLM".
- **Fix:** Replace with `ConcurrentDictionary<string,string>`, or load into a new dict and
  swap the field atomically (`_files = newDict` — reference assignment is atomic on refs).
  Same applies to `SkillCatalog` which is also reloaded from admin endpoints.

### SqliteConversationStore compaction fires fire-and-forget with `CancellationToken.None` (severity: high)
- **Location:** `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:404-430`
- **Issue:** `TryStartCompaction` is invoked from the synchronous `Update()` method on the
  hot request path. It schedules `Task.Run(async () => await RunCompactionAsync(...))` with
  no cancellation wiring, no lifetime tie-in to `IHostApplicationLifetime`, and no back-pressure.
  The background task makes an LLM call (`CompactionSummarizer.cs:62`) that can take many
  seconds to minutes. If the process is shutting down, nothing signals the compaction to
  stop — it keeps holding a SQLite connection and an HTTP client on the way out.
  Additionally, the `finally` block writes `UpdateCompactionState(compactionRunning: false,
  context: null, compactedUpToRowId: null)` — when `RunCompactionAsync` succeeded, it already
  set the context+cutoff, and this clears them back to null because
  `UpdateCompactionState` only writes the columns you pass non-null. Read
  `UpdateCompactionState` more carefully: the SET clause always writes
  `CompactionRunning = @running`, but `context` and `compactedUpToRowId` are only written
  when not null. So the `finally` block resets `CompactionRunning` to 0 without clobbering
  what `RunCompactionAsync` just saved — OK on success. **But on exception**
  `RunCompactionAsync` never wrote the context, and CompactionRunning stays 0 — so a second
  attempt will retry cleanly. Actual risk is narrower than it looks, but the cancellation
  + lifetime issues remain real.
- **Risk:** Graceful shutdown can orphan in-flight compactions; SQLite WAL checkpoint
  blocks indefinitely; on container restart the caller sees a stuck state. Test flakiness
  if a test finishes before the background task drains.
- **Fix:** Inject `IHostApplicationLifetime`, pass `ApplicationStopping` as the CT to
  `RunCompactionAsync`; or move compaction into a proper `IHostedService` that drains
  pending conversations on shutdown. Least-change option: gate `Task.Run` on a
  `_shutdownCts` the store owns and dispose on dispose.

### ConnectionManager.StartConnectionAsync is check-then-act (severity: high)
- **Location:** `src/agent/OpenAgent/ConnectionManager.cs:72-92`
- **Issue:** `if (_running.ContainsKey(connectionId)) return;` followed by `provider =
  factory.Create(connection); await provider.StartAsync(ct); _running[connectionId] =
  provider;`. Two concurrent requests (UI "Start" + auto-start on save, or two HTTP
  restart requests during a reload) can both see the key absent, both create providers,
  both start them, and the second overwrites the first in the dict. The losing provider is
  never stopped or disposed — it's a leaked connection to Telegram/WhatsApp/etc. with an
  active polling loop and open HTTP client.
- **Risk:** Duplicate Telegram polling (duplicate replies!), a zombie Baileys node
  subprocess, leaked HTTP client handles, IWebSocketRegistry entries pointing at dead
  sockets.
- **Fix:** Use `ConcurrentDictionary<string,SemaphoreSlim>` for per-connection lifecycle
  locks, or fold lifecycle into a `GetOrAdd` with a factory that starts, then catch the
  loser via `TryAdd` and `await loser.StopAsync`. The same pattern already exists in
  `VoiceSessionManager.cs:38-42` — copy it.

### WebSocketRegistry.Register silently drops the previous socket (severity: medium)
- **Location:** `src/agent/OpenAgent/WebSocketRegistry.cs:17-20`
- **Issue:** Indexer assignment `_sockets[conversationId] = webSocket` overwrites an existing
  entry without closing or notifying the previous socket. A user who reconnects while an
  older WebSocket is still `Open` (e.g. page reload over a flaky network) silently loses
  the old socket's registration — but the old socket is still open on the server and the
  old endpoint loop keeps running. DeliveryRouter will now push scheduled-task output to
  the new socket only, while the old socket stays open until the browser tears it down or
  a read/write fails.
- **Risk:** Duplicate bridges for the same conversation, memory leak on connection churn,
  inconsistent delivery (scheduled-task results posted to the "wrong" socket).
- **Fix:** On register, `_sockets.AddOrUpdate(...)` with a factory that captures the old
  value and triggers a graceful close (best-effort `CloseAsync(PolicyViolation, ...)`).
  Mirror the eviction pattern in `WebSocketTerminalEndpoints.cs:51-56` which does this
  correctly for terminals.

### ExtractEmbeddedWwwroot is not idempotent-safe across concurrent instances (severity: medium)
- **Location:** `src/agent/OpenAgent/Program.cs:50-69`
- **Issue:** On every startup the code deletes `AppContext.BaseDirectory/wwwroot`
  recursively and re-extracts the embedded zip. Two processes launched against the same
  install directory (e.g. the integration test runner on a dev box while the installed
  service is running — or two test runs in parallel) can race: one is mid-extract while
  the other deletes the tree. Windows also routinely fails
  `Directory.Delete(path, recursive:true)` when files are held open by any process
  (antivirus scan, indexer), which is exactly the scenario the `UseStaticFiles` middleware
  creates once it has served a file once. The outer `try/catch` writes to stderr but the
  host then starts without a wwwroot. Also: this runs **before** Serilog is configured, so
  failures only surface on stderr — invisible in production logs.
- **Risk:** On Windows service upgrade, if the service is running and a dev also launches
  the exe in the same folder, wwwroot gets hosed. UI becomes unreachable until restart.
- **Fix:** Compute an integrity hash of the embedded zip; skip extract if wwwroot already
  contains a sentinel `.zip-hash` matching. Extract to a temp sibling folder then
  atomic-rename in. Also log via Serilog after config completes, or capture the error and
  surface it later.

### Bootstrap writes config/agent.json before ApiKeyResolver can read it (severity: medium)
- **Location:** `src/agent/OpenAgent/DataDirectoryBootstrap.cs:63-65` vs.
  `Program.cs:87, 90, 200`
- **Issue:** The happy path is fine: `DataDirectoryBootstrap.Run()` creates
  `{dataPath}/config/agent.json` with `{"symlinks": {}}` if missing, then
  `ApiKeyResolver.Resolve` reads it. But bootstrap is invoked on line 90 while
  `Directory.CreateDirectory(environment.DataPath)` is on line 87 — this is correct for
  the root but neither creates `{dataPath}/config/` up-front before bootstrap; bootstrap
  itself creates `config/` on line 33 (`RequiredDirectories` loop) which runs before the
  agent.json write on line 64, so the ordering happens to work. The failure mode is if
  `RequiredDirectories` is reordered or `config/` is renamed. **More importantly,** when
  `ApiKeyResolver` persists a new key back to agent.json, it clobbers the `{"symlinks":
  {}}` seeded by bootstrap — verify this round-trip preserves `symlinks` or the
  `DataDirectoryBootstrap` junction-creation path permanently regresses on the second run
  after the agent.json is overwritten by `ApiKeyResolver`.
- **Risk:** Silent loss of `symlinks` configuration if `ApiKeyResolver` rewrites agent.json
  without a read-merge-write cycle.
- **Fix:** Make `ApiKeyResolver.Resolve` do read-merge-write (load existing JSON, set the
  `apiKey` property, write back) rather than serialising a fresh object. Add a test that
  asserts `symlinks` survives key persistence.

### VoiceSessionManager leaks the session on a race loser (severity: low)
- **Location:** `src/agent/OpenAgent/VoiceSessionManager.cs:22-45`
- **Issue:** If two concurrent callers `GetOrCreateSessionAsync` for the same conversation,
  both reach `provider.StartSessionAsync` (which opens a WebSocket to Azure OpenAI
  Realtime). The loser calls `await session.DisposeAsync()` — fine. But between `TryAdd`
  returning false and `DisposeAsync` completing, the loser pays the cost of a full session
  handshake against the remote API. Rare, but can cause 429s or double-billing if a client
  retries aggressively.
- **Risk:** Low. The correctness is fine; just wastes a session slot per race.
- **Fix:** Use `GetOrAdd(conversationId, _ => Lazy<Task<IVoiceSession>>)` to coalesce
  concurrent creators.

### Scheduled-tasks service allocates timer state that can stomp during Tick (severity: low)
- **Location:** `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs:76, 229-263`
- **Issue:** `new Timer(_ => _ = TickAsync(), ...)` fires ticks on the threadpool. If a
  tick takes longer than 30s (not unlikely with a slow LLM round-trip per task), a second
  tick can fire while the first is still running. Both acquire `_lock`, compute due tasks
  from the same store snapshot, and can both pick up the same due task — executing it
  twice before `NextRunAt` is advanced. Mitigation: `ExecuteTaskAsync` picks tasks from the
  dict then runs outside the lock. First tick's update wins on the SQLite side, but the
  second tick already grabbed its reference — you still get two runs.
- **Risk:** Duplicate scheduled-task runs under load / slow LLM. User sees the same
  reminder twice.
- **Fix:** Gate `TickAsync` with `Interlocked.CompareExchange(ref _ticking, 1, 0) == 0` so
  only one tick runs at a time. Also take-and-clear: within the lock, move due tasks to a
  "running" set and filter them out of future ticks until execution completes.

### TelegramWebhookEndpoints uses `Task.Run` without awaiting (severity: low)
- **Location:** `src/agent/OpenAgent.Channel.Telegram/TelegramWebhookEndpoints.cs:70-81`
- **Issue:** Processing is intentionally fire-and-forget to unblock the HTTP response to
  Telegram. But the launched task is not tied to `IHostApplicationLifetime` — on
  shutdown, it just dies mid-LLM-call. Also uses `CancellationToken.None` ignoring
  `context.RequestAborted`.
- **Risk:** Dropped replies on restart. Hard to test because the HTTP request has long
  since returned by the time the background task executes.
- **Fix:** Queue into an `IHostedService`-owned `Channel<Update>` with graceful drain, or
  at least track the handles in a `ConcurrentBag<Task>` and `WhenAll` them on shutdown
  with a timeout.

## Smells

### Keyed-DI wiring is repetitive and bespoke (severity: medium)
- **Location:** `src/agent/OpenAgent/Program.cs:121-222` (100+ lines of wiring)
- **Issue:** Each provider is wired three times: (1) keyed singleton registration, (2)
  forwarding `IConfigurable` registration via a lambda that calls `GetRequiredKeyedService`,
  (3) sometimes a non-keyed default forwarder (lines 177-180). Adding a new provider is a
  mechanical five-line addition in Program.cs. This invites copy-paste errors, and indeed:
  embedding providers only have two registrations (144-153) while voice providers have three
  (169-171, 179-180, 193-197) and text providers have four (167-168, 172, 177-178, 189-191).
- **Suggestion:** Extract `IServiceCollection.AddLlmTextProvider<T>()` and similar extension
  methods in each provider project (CLAUDE.md already mentions `AddApiKeyAuth` as the
  model). Program.cs should be a one-line-per-capability composition.

### Endpoints violate the "thin" rule in several places (severity: medium)
- **Location:** `src/agent/OpenAgent.Api/Endpoints/FileExplorerEndpoints.cs:106-255` (rename,
  upload, mkdir), `LogEndpoints.cs:59-131` (filter/paging logic), `AdminEndpoints.cs:83-113`
  (JSON merge logic)
- **Issue:** FileExplorerEndpoints embeds path-safety, rename-collision, form handling,
  multipart copy streaming, and directory-traversal checks inline in the endpoint lambdas.
  LogEndpoints embeds level/search/tail/since/until filtering plus JSON parsing. AdminEndpoints
  does JSON merge so partial updates work. These are legitimately domain logic, and would
  benefit from a service layer — they're just the worst candidates for the current
  "endpoints stay thin" invariant.
- **Suggestion:** Introduce `FileExplorerService`, `LogService`, and keep the endpoints as
  projections. Or accept these as utility endpoints and document the carve-out.

### XML doc comments inside method bodies (severity: low)
- **Location:** `src/agent/OpenAgent.Api/Endpoints/AdminEndpoints.cs:38-40, 50-53`
- **Issue:** Triple-slash `/// <summary>` blocks inside `MapAdminEndpoints` body. Compiles
  but has no effect (doc comments on statements don't emit docs). Looks like an AI assistant
  thought it was annotating a method.
- **Suggestion:** Demote to `//` comments.

### `AgentConfig.MainConversationId` is dead config (severity: low)
- **Location:** `src/agent/OpenAgent.Models/Configs/AgentConfig.cs:54-55`,
  `AgentConfigConfigurable.cs:52-55`
- **Issue:** The property is declared, loaded, and serialised. No other code reads it. The
  comment on the property documents its intended behaviour (fallback conversation for
  scheduled tasks), but `ScheduledTaskToolHandler.CreateScheduledTaskTool` always binds to
  the current conversation (`ScheduledTaskToolHandler.cs:89-98`) and the executor/router
  never consult it.
- **Suggestion:** Either wire it up or delete it. Leaving it as admin-editable-but-unused
  is actively misleading.

### `ConversationType` doesn't match CLAUDE.md promises (severity: low)
- **Location:** `src/agent/OpenAgent.Models/Conversations/Conversation.cs:5-9`
- **Issue:** CLAUDE.md lists `Text`, `Voice`, `ScheduledTask`, `WebHook` as enum values.
  Actual enum has only `Text` and `Voice`. Scheduled tasks masquerade as `Text`
  conversations with source `"scheduledtask"` (ScheduledTaskExecutor.cs:63). If the
  system-prompt behaviour is supposed to differ by type, it doesn't today —
  `SystemPromptBuilder.FileMap` has no `ScheduledTask` entry anyway.
- **Suggestion:** Align the enum to reality (drop the doc promise) or add the missing
  types and wire prompt variants.

### ChatEndpoints / WebSocketTextEndpoints bypass the `Func<string, ILlmTextProvider>` resolver (severity: low)
- **Location:** `ChatEndpoints.cs:42`, `WebSocketTextEndpoints.cs:96`
- **Issue:** Both files take `IServiceProvider services` as a parameter and call
  `services.GetRequiredKeyedService<ILlmTextProvider>(conversation.Provider)` directly. This
  works because every provider is keyed, but it diverges from the idiom that CLAUDE.md and
  every other callsite uses (`TelegramMessageHandler.cs:133`, `ScheduledTaskExecutor.cs:79`,
  `CompactionSummarizer.cs:58`). The `Func<string, ILlmTextProvider>` is registered
  (Program.cs:172) but unused from Api endpoints.
- **Suggestion:** Replace `IServiceProvider services` with
  `Func<string, ILlmTextProvider> textProviderResolver` parameter. Consistent with the
  documented pattern and makes the endpoint easier to test.

### `ChatEndpoints` uses anonymous types for JSON output (severity: low)
- **Location:** `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs:57-63`
- **Issue:** Coding conventions in CLAUDE.md: "never anonymous types for API payloads".
  `new { type = "text", delta.Content }` breaks the rule. Also there's a typed envelope
  available (`OpenAgent.Models/Text/ChatContracts.cs` — not read here but referenced by
  the namespace `using`).
- **Suggestion:** Introduce typed records for each REST event shape, reuse them.

### `AdminEndpoints` infers capability tags via `is` checks (severity: low)
- **Location:** `src/agent/OpenAgent.Api/Endpoints/AdminEndpoints.cs:117-123`
- **Issue:** `InferCapabilities` checks `if (c is ILlmTextProvider) ...; if (c is
  ILlmVoiceProvider) ...` — the flat `IConfigurable` interface intentionally doesn't
  expose capability, but the endpoint wants it anyway. Adding a new capability (e.g.
  embedding) requires editing this helper. Today `OnnxBgeEmbeddingProvider` is **not**
  registered as `IConfigurable` at all (Program.cs:149-153), so it's invisible to the
  admin UI. That's either a bug (hidden embedding settings) or a design choice.
- **Suggestion:** Add an optional `Capability { get; }` to `IConfigurable` (default empty
  array), or register a small `IConfigurableDescriptor` alongside each provider.

### `SystemPromptEndpoints` lives in the host, not in `OpenAgent.Api/Endpoints/` (severity: low)
- **Location:** `src/agent/OpenAgent/SystemPromptEndpoints.cs`
- **Issue:** Every other endpoint lives in `OpenAgent.Api/Endpoints/`. This one is in the
  host project, likely because it needs access to `SystemPromptBuilder` (which is host-only).
  The project rules say "extract an interface into Contracts"; here the fix is either to
  introduce `ISystemPromptBuilder` in Contracts or leave this as the documented exception.
- **Suggestion:** Mirror the `IVoiceSessionManager` pattern — interface in Contracts,
  concrete in host, endpoint in Api.

### `OpenAgent.Api` directly references `OpenAgent.ScheduledTasks` and `OpenAgent.Models.Tools` (severity: low)
- **Location:** `src/agent/OpenAgent.Api/Endpoints/ScheduledTaskEndpoints.cs:3-5`
- **Issue:** `ScheduledTaskEndpoints` imports `OpenAgent.ScheduledTasks` and uses
  `ScheduledTaskService` (a concrete class) directly. The service is registered as itself
  (`ServiceCollectionExtensions.cs:33-38`), not behind an interface. CLAUDE.md explicitly
  calls out: "When `OpenAgent.Api` needs a type from the host project, extract an
  interface into `OpenAgent.Contracts`." Scheduled tasks is not technically the host, but
  it's a feature module — the rule applies. Compare to `ConnectionEndpoints` which depends
  on `IConnectionManager` from Contracts.
- **Suggestion:** Extract `IScheduledTaskService` into Contracts. Worth it once the
  "system jobs" abstraction hinted at in CLAUDE.md lands.

### `SystemPromptBuilder` silently picks Europe/Copenhagen (severity: low)
- **Location:** `src/agent/OpenAgent/SystemPromptBuilder.cs:153`
- **Issue:** `TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen")` throws on a host
  where that timezone database entry is absent. Linux containers have it via tzdata; on
  Windows it's available in modern .NET ICU builds, but an `unknown-timezone` failure
  mode is undefined. Also: it's hardcoded; users in other timezones get Copenhagen time
  in their system prompts.
- **Suggestion:** Wrap in try/catch + fallback to UTC; make the timezone a config value.

### `ScheduledTaskService` swallows exceptions in `TickAsync` (severity: low)
- **Location:** `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs:258-263`
- **Issue:** The comment says "Catch all to prevent the timer from dying" — legitimate.
  But there's no escalation beyond `LogError`. A pathological `ScheduleCalculator` bug
  (e.g. NaN next-run time) will retry on every 30s tick forever, spamming logs and never
  falling back to disable.
- **Suggestion:** On 3+ consecutive tick-level exceptions, disable the tick timer until
  an admin restarts or flips a flag.

### `Program.cs` is doing a lot (severity: low)
- **Location:** `src/agent/OpenAgent/Program.cs` (~290 lines)
- **Issue:** It's near the edge of "tolerable big wiring file". Install-mode dispatch,
  wwwroot extract, hosting config, Serilog setup, DI for every project, app.Lifetime
  hooks, middleware wiring, and endpoint mapping all live together. Adding a
  feature typically requires edits in four spots within this file.
- **Suggestion:** Extract `IServiceCollection.AddOpenAgentCore()` extensions per feature
  area (providers, channels, scheduled tasks, memory index) — each returning
  `IServiceCollection` so Program.cs becomes a sequence of `services.AddFoo()` calls.

### Bootstrap's BOOTSTRAP.md "first-run" detection is brittle (severity: low)
- **Location:** `src/agent/OpenAgent/DataDirectoryBootstrap.cs:41, 54-55`
- **Issue:** `isFirstRun = !File.Exists(AGENTS.md)`. If a user deletes AGENTS.md to
  regenerate it, they get a BOOTSTRAP.md too — and then AGENTS.md instructs the agent to
  run the ritual again. A sentinel `.bootstrap-complete` file would be more durable.
- **Suggestion:** Write a `config/.bootstrap-complete` on first successful run; gate
  BOOTSTRAP.md extraction on its absence.

## Open Questions

- **Compaction lifecycle ownership.** `SqliteConversationStore.TryStartCompaction` feels
  like it should be a `IHostedService` (own its own lifetime, drain on shutdown). The
  current "fire from inside Update() via Task.Run" is the quickest thing that works but
  harder to test and reason about. Is this a conscious staging step, or just historical?
- **Why is `OnnxBgeEmbeddingProvider` not registered as `IConfigurable`?** (Program.cs
  144-153). `OnnxMultilingualE5EmbeddingProvider` isn't either. If the admin UI should
  manage embedding model selection, this is a gap. If it shouldn't, the
  `AgentConfigConfigurable` `embeddingProvider` / `embeddingModel` fields are misleading.
- **Is `conversation.Source` used for anything?** `AgentLogic.cs:20-22` has a TODO
  acknowledging `source` is ignored. If never wired, drop the parameter; if planned,
  document the roadmap and mark it required for channel providers to fill in correctly.
- **Should `ConversationType` grow `ScheduledTask` and `WebHook`?** CLAUDE.md promises it;
  reality doesn't. Either make the doc match the code or the code match the doc.
- **Are there any invariants around ordering of IConfigurable.Configure() calls at
  startup?** Program.cs:240-245 iterates `GetServices<IConfigurable>()` in registration
  order. If the conversation store needs the compaction summarizer configured first, or
  vice versa, it's not enforced.
- **`POST /api/conversations/` creates a conversation** (ConversationEndpoints.cs:22-27).
  CLAUDE.md says "No dedicated create conversation endpoint". The reality contradicts the
  documented architecture. Intentional?

## Files reviewed

- `src/agent/OpenAgent/Program.cs`
- `src/agent/OpenAgent/AgentLogic.cs`
- `src/agent/OpenAgent/SystemPromptBuilder.cs`
- `src/agent/OpenAgent/SystemPromptEndpoints.cs`
- `src/agent/OpenAgent/AgentConfigConfigurable.cs`
- `src/agent/OpenAgent/DataDirectoryBootstrap.cs`
- `src/agent/OpenAgent/LoggingConfig.cs`
- `src/agent/OpenAgent/RootResolver.cs`
- `src/agent/OpenAgent/VoiceSessionManager.cs`
- `src/agent/OpenAgent/ConnectionManager.cs`
- `src/agent/OpenAgent/WebSocketRegistry.cs`
- `src/agent/OpenAgent/OpenAgent.csproj`
- `src/agent/OpenAgent.Contracts/*.cs` (all 15 interfaces)
- `src/agent/OpenAgent.Models/Common/CompletionEvent.cs`
- `src/agent/OpenAgent.Models/Common/CompletionOptions.cs`
- `src/agent/OpenAgent.Models/Configs/AgentConfig.cs`
- `src/agent/OpenAgent.Models/Configs/GlobalConfig.cs`
- `src/agent/OpenAgent.Models/Conversations/Conversation.cs`
- `src/agent/OpenAgent.Models/Conversations/Message.cs`
- `src/agent/OpenAgent.Models/Conversations/ConversationResponses.cs`
- `src/agent/OpenAgent.Models/Conversations/CompactionConfig.cs`
- `src/agent/OpenAgent.Models/Connections/Connection.cs`
- `src/agent/OpenAgent.Models/Voice/VoiceEvent.cs`
- `src/agent/OpenAgent.Api/Endpoints/ConversationEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/AdminEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/ConnectionEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/WebSocketTextEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/ScheduledTaskEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/ToolEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/LogEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/FileExplorerEndpoints.cs`
- `src/agent/OpenAgent.Compaction/CompactionSummarizer.cs`
- `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs`
- `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskExecutor.cs`
- `src/agent/OpenAgent.ScheduledTasks/DeliveryRouter.cs`
- `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskToolHandler.cs`
- `src/agent/OpenAgent.ScheduledTasks/ServiceCollectionExtensions.cs`
- `src/agent/OpenAgent.ScheduledTasks/Storage/ScheduledTaskStore.cs`
- `src/agent/OpenAgent.ScheduledTasks/Models/ScheduledTask.cs`
- `src/agent/OpenAgent.ConfigStore.File/FileConfigStore.cs`
- `src/agent/OpenAgent.ConfigStore.File/FileConnectionStore.cs`
- `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramWebhookEndpoints.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs` (architectural read)
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppEndpoints.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
