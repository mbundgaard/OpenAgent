# Channels Review — 2026-04-23

Scope: `OpenAgent.Channel.Telegram` (all .cs + webhook endpoint), `OpenAgent.Channel.WhatsApp` (all .cs + `node/baileys-bridge.js`), `OpenAgent.Channel.Telnyx`, `OpenAgent/ConnectionManager.cs`, `OpenAgent.ConfigStore.File/FileConnectionStore.cs`, `IChannelProvider`, `IChannelProviderFactory`, `IOutboundSender`.

## TL;DR

The two shipping channels (Telegram, WhatsApp) work, but advertised access-control knobs (`AllowedUserIds`, `AllowedChatIds`) are parsed and never consulted — the only real gate is the once-per-connection `AllowNewConversations` boolean, which has a check-then-act race under concurrent webhook delivery and loses the first unseen-chat message after auto-lock. Telegram webhook mode regenerates `_webhookSecret` on every `StartAsync` (never persisted), so `SetWebhook` + inbound validation only line up after Telegram retries. The WhatsApp Node bridge is robust enough, but `WhatsAppNodeProcess.StartAsync` ignores its `_scriptPath` argument, `WriteAsync` silently drops commands if the channel is completed, and `StopAsync` calls `Process.WaitForExit(TimeSpan)` on the stdin-driven ReadLine loop which can deadlock the shutdown path. `ConnectionManager.StopAsync` is strictly sequential and blocks until every child settles — combined with a 5 s hard Node kill and a 2 s task join, host shutdown can take double-digit seconds per connection. `Telnyx` is a ghost: the folder contains only `bin/`, `obj/`, and a stale build output; there is no source, no csproj, no factory, no endpoint — nothing to review. Telegram HTML rendering is safe; WhatsApp markdown output is not escaped at all and reflects raw LLM content (e.g., `*foo*`) into WhatsApp's own control syntax.

## Strengths

- **Per-chat conversation isolation is clean.** `{channelType}:{connectionId}:{chatId}` is threaded consistently through store lookups; group vs. DM handled in handler (`TelegramMessageHandler.cs:63-70`, `WhatsAppMessageHandler.cs:114-119`).
- **Typed factory metadata** drives dynamic forms without hardcoding channel knowledge in the UI (`TelegramChannelProviderFactory.cs:25-33`, `WhatsAppChannelProviderFactory.cs:29-36`).
- **Webhook secret compared constant-time.** `TelegramWebhookEndpoints.cs:47-49` uses `CryptographicOperations.FixedTimeEquals`.
- **Sender abstraction** (`ITelegramSender`, `IWhatsAppSender`) allows handler-level unit testing without faking `ITelegramBotClient`/Baileys.
- **Dedup cache with TTL + bounded eviction.** `WhatsAppMessageHandler.cs:184-222` prevents re-processing and caps memory.
- **Exponential backoff with attempt reset on long-lived sessions.** `WhatsAppChannelProvider.cs:350-399`; LoggedOut correctly short-circuits (no retry, creds wiped).
- **Stdin serialization via `Channel<string>` single-consumer** (`WhatsAppNodeProcess.cs:205-206, 382-402`) avoids interleaved writes on `stdin`.
- **Markdown AST conversion rather than regex.** Both converters use Markdig; Telegram converter HTML-encodes literals (`TelegramMarkdownConverter.cs:246`) and strips non-http(s) link schemes (`:306-320`).
- **Node bridge has uncaught handlers** and routes logs to stderr only (`baileys-bridge.js:92-99`), preventing a rogue console.log from poisoning the JSON-line protocol.

## Bugs

### Advertised access control is dead code (severity: high)

- **Location:** `TelegramOptions.cs:12-15`, `TelegramChannelProviderFactory.cs:61-78`, `TelegramMessageHandler.cs` (no reference); `WhatsAppOptions.cs:8-14`, `WhatsAppChannelProviderFactory.cs:63-80`, `WhatsAppMessageHandler.cs` (no reference).
- **Issue:** `TelegramOptions.AllowedUserIds` and `WhatsAppOptions.AllowedChatIds` are deserialized, exposed through test fixtures (`TelegramMessageHandlerTests.cs:11-38`), and even used to construct options — but nothing in either handler ever reads them. WhatsApp's handler doesn't even receive `WhatsAppOptions` at all (`WhatsAppMessageHandler` ctor, `WhatsAppMessageHandler.cs:34-48`). `WhatsAppChannelProvider._options` is stored in the field and never referenced elsewhere (only write: `WhatsAppChannelProvider.cs:73`).
- **Risk:** Operators who populate `allowedUserIds`/`allowedChatIds` believing those lists restrict access get no protection. The only live gate is `AllowNewConversations` (auto-locks after first conversation), which is a one-time admission and cannot express an allowlist. Tests are green because they never exercise the rejection path — `TelegramMessageHandlerTests` with `BlockedUserId` still relies on `AllowNewConversations=false` to drop the message.
- **Fix:** Either delete the fields and update CLAUDE.md (current claim: "empty allowlist = allow all" — this matches behavior incidentally, but only because the list is ignored entirely) or wire the check into both handlers (`chatId`/`userId` vs. list; empty = allow). If restriction is intended as "runtime management concern," replace with a persisted `AllowedChatIds` on `Connection` and surface a UI for it.

### Webhook secret regenerated on every start (severity: high)

- **Location:** `TelegramChannelProvider.cs:107-116`.
- **Issue:** `_webhookSecret = _options.WebhookSecret ?? Guid.NewGuid().ToString("N");` — when `WebhookSecret` is not configured (the normal path, since the factory's `ConfigFields` at `TelegramChannelProviderFactory.cs:25-31` never exposes it), the provider generates a new GUID on every `StartAsync`. It then calls `SetWebhook` with that new secret and validates inbound webhooks against `_webhookSecret`. But the newly generated secret is never persisted — on restart, the value is lost. The webhook ID is persisted (`:92-100`) but the secret isn't.
- **Risk:** (1) Because the provider sets a fresh secret in the same call, Telegram updates the expected header; the old secret is immediately rotated. No in-flight updates are forged-validated. (2) However, any inbound request already in flight when the restart happens will be rejected (stale secret). (3) The secret in-memory is not reachable for operator debugging; if `SetWebhook` fails, the system can end up with Telegram holding a different secret than the running provider thinks. Symptom: silent 401s from the bot with no way to reconcile without a clean restart. (4) If `ConnectionEndpoints` restart flow runs (`ConnectionEndpoints.cs:98-120` — stop + start on update), every config tweak burns a new secret. Good from a security posture (rotation), bad because the same mechanism also loses inbound updates mid-flight.
- **Fix:** Persist `_options.WebhookSecret` back to the `Connection.Config` alongside `webhookId` in `:87-100`. Also add `webhookSecret` to `ConfigFields` (or keep it internal but at least store it).

### ConnectionManager sequential stop with blocking waits (severity: high)

- **Location:** `ConnectionManager.cs:55-68`, `WhatsAppNodeProcess.cs:258, 287`.
- **Issue:** `StopAsync` iterates `_running` and awaits each `provider.StopAsync(ct)` in series. Each `WhatsAppChannelProvider.StopAsync` calls `WhatsAppNodeProcess.StopAsync`, which `Process.WaitForExit(TimeSpan.FromSeconds(5))` on the main thread, then `Task.WhenAll(...).WaitAsync(TimeSpan.FromSeconds(2))` on the stdout/stderr/stdin readers. `Process.WaitForExit(TimeSpan)` is **synchronous and blocking**; the `await WriteAsync(FormatShutdownCommand())` above it writes into an unbounded `Channel<string>` that may not have been drained yet — no guarantee the shutdown command ever hits stdin before the kill.
- **Risk:** Host shutdown takes (N × 5 s) worst case for WhatsApp channels; Telegram polling stop is cheap (`Cancel()` + `Dispose()`) but Telegram webhook stop is a no-op (see design decision in CLAUDE.md). If a production deployment has 3 WhatsApp connections and a Kestrel shutdown signal arrives, the `GracefulShutdownTimeout` can be exceeded. Azure App Service kills the container after ~30 s.
- **Fix:** (1) Parallelize `StopAsync` with `Task.WhenAll(_running.Values.Select(p => p.StopAsync(ct)))`. (2) Replace `_process.WaitForExit(TimeSpan)` with `await _process.WaitForExitAsync(linkedCts.Token)` driven by an internal timeout. (3) Write the shutdown command with `StandardInput.WriteLineAsync` directly (bypassing the channel) and `FlushAsync` before waiting — otherwise the drain task may not have pulled it off the queue yet.

### WhatsAppNodeProcess ignores injected scriptPath (severity: medium)

- **Location:** `WhatsAppNodeProcess.cs:78, 104, 177`.
- **Issue:** Constructor takes `scriptPath` and stores `_scriptPath`, but `StartAsync` overrides it: `var resolvedScript = Path.Combine(AppContext.BaseDirectory, "node", "baileys-bridge.js");`. `_scriptPath` is never read.
- **Risk:** The field is dead state. Testing paths that want a different bridge script can't inject one. Also, if the Windows service is installed where `AppContext.BaseDirectory` isn't the exe dir (e.g., if the service copies files), the script isn't found; there's no fallback to `_scriptPath`.
- **Fix:** Either remove the constructor parameter or use it: `var resolvedScript = _scriptPath;`.

### WriteAsync silently drops commands (severity: medium)

- **Location:** `WhatsAppNodeProcess.cs:221-229`.
- **Issue:** `TryWrite` returns `false` if the channel is completed (`_stdinChannel?.Writer.TryComplete()` is called from `StopAsync`), but the return value is discarded. After stop is initiated, any racing message send from a pending LLM completion or scheduled task silently disappears. There is no signal to callers that the write was lost.
- **Risk:** `WhatsAppChannelProvider.SendMessageAsync` (used by `IOutboundSender` for scheduled tasks — `WhatsAppChannelProvider.cs:205-211`) completes successfully when the command was actually dropped. Scheduled-task delivery reports success; user never gets the message.
- **Fix:** Change `TryWrite` to `WriteAsync`, or at minimum throw `InvalidOperationException` if the writer rejects and propagate via `WriteAsync` to the caller. Log at least a warning.

### Reconnect recursion with unbounded closures (severity: medium)

- **Location:** `WhatsAppChannelProvider.cs:382-398`.
- **Issue:** `ScheduleReconnectAsync` catches a failure and does `_ = Task.Run(ScheduleReconnectAsync);` — a recursive schedule. The attempt counter bounds it (`_reconnectAttempts >= 10` at `:365`), but the recursive chain captures nothing, so no runaway closures. But each invocation creates a new `WhatsAppNodeProcess` via `StartNodeProcessAsync` (`:391`), and the previous `_nodeProcess` is reassigned without explicit disposal check — the `try { await _nodeProcess.StopAsync(); } catch { }` on `:385-389` uses the same field that's about to be overwritten. If `StopAsync` throws *after* assigning the new process, the old process is orphaned. Also, `_pingTimer?.Dispose()` happens inside `StartNodeProcessAsync` (`:238-239`), but reconnect never cancels the *old* timer before replacement — it's disposed in-place, which is fine, but any in-flight ping callback racing with the new timer could see the wrong `_nodeProcess` reference (no lock on that read — `PingTimerCallback` uses `_nodeProcess` outside `_lock`).
- **Risk:** Zombie Node children under reconnect stress. In worst case, the ping callback writes to a disposed process's stdin and logs a warning, then the new timer gets scheduled correctly — low impact, but messy.
- **Fix:** Serialize the `_nodeProcess` swap under `_lock`. Explicitly dispose the old before assigning new. Read `_nodeProcess` once per callback into a local.

### ConnectionManager race on duplicate Start (severity: medium)

- **Location:** `ConnectionManager.cs:72-92`.
- **Issue:** `StartConnectionAsync` checks `_running.ContainsKey` then creates + starts the provider, then sets `_running[connectionId] = provider;`. Two concurrent `POST /api/connections/{id}/start` requests can both pass the check, both create providers, and the second `TryAdd`-style assignment clobbers the first. The first provider becomes unreferenced but *running* — the Node child leaks, the Telegram polling task leaks.
- **Risk:** Leaked providers under concurrent API abuse or a rapid double-click in the UI. Combined with WhatsApp's auth dir sharing — two Baileys instances on the same auth dir is a Baileys invariant violation.
- **Fix:** Use `_running.GetOrAdd(connectionId, ...)` with an async-compatible pattern (or guard the section with a `SemaphoreSlim` per-connection-id; or skip and warn if concurrent).

### FindOrCreateChannelConversation race (severity: medium)

- **Location:** `SqliteConversationStore.cs:168-219` (note: outside review scope but reached from channels), `TelegramMessageHandler.cs:88-102`, `WhatsAppMessageHandler.cs:76-90`.
- **Issue:** No `UNIQUE` index on `(ChannelType, ConnectionId, ChannelChatId)` (`SqliteConversationStore.cs:50-58`). `FindOrCreate` is a read-then-write. Two concurrent messages for the same chat (Telegram webhook dispatcher fires each `Task.Run` in parallel) can both `FindChannelConversation` → null, both take the gating branch, both call `FindOrCreateChannelConversation`, both INSERT a new conversation.
- **Risk:** Duplicate conversations bound to the same chat. Subsequent `FindChannelConversation` returns whichever row the reader picks; messages fork across the two rows. Auto-lock of `AllowNewConversations` (`TelegramMessageHandler.cs:99`) also runs twice — harmless on the second write but wasteful. Under Telegram polling the `ReceiverOptions` with a single handler call stays serial per updates batch, but the webhook dispatcher goes parallel (`TelegramWebhookEndpoints.cs:70-81`).
- **Fix:** Add `UNIQUE (ChannelType, ConnectionId, ChannelChatId)` index in the schema migration; change `FindOrCreate` to `INSERT ... ON CONFLICT DO NOTHING RETURNING *` or wrap in a transaction with `INSERT OR IGNORE` + re-select.

### AllowNewConversations check-then-act race loses messages (severity: medium)

- **Location:** `TelegramMessageHandler.cs:89-102`, `WhatsAppMessageHandler.cs:77-90`.
- **Issue:** Two concurrent messages (from different chats, same connection, both first-contact) both read `AllowNewConversations=true`, both proceed, both set it false, both save the connection. Not a race in the obvious sense (both succeed), but: if three chats hit simultaneously on first start, *all three* may get conversations — defeating the "auto-lock after first" intent. Conversely, if the first message's gating branch runs after a *second* message's auto-lock save has already flipped the flag, the first message (already admitted conceptually) lands on `FindOrCreate` with a conversation it just created.
- **Risk:** Mostly harmless but the feature doesn't do what CLAUDE.md claims ("auto-lock after the first conversation"). Also, connection state is read from disk on every message via `_connectionStore.Load(_connectionId)` — if the file store has a stale read during a concurrent write (`FileConnectionStore.Save` holds a lock, but reads overlap between saves), correctness relies on `SemaphoreSlim`. Checked — it does. OK.
- **Fix:** Either document "auto-lock is best-effort" or serialize per connection (a `SemaphoreSlim` on `_connectionId` in the handler). Alternatively: make the gate a separate "admitted chats set" stored on the connection, and perform a single CAS-style update.

### WhatsApp markdown output is not escaped (severity: medium)

- **Location:** `WhatsAppMarkdownConverter.cs:242-243` (literals), `:252-258` (inline code), `:266-269` (HtmlInline pass-through).
- **Issue:** WhatsApp treats `*`, `_`, `~`, backtick as formatting markers. The converter copies literal content verbatim (`sb.Append(literal.Content.ToString());`). If an LLM response contains raw `*` or `_` in text (e.g., quoting a filename like `a*b_c`), those characters are reinterpreted by WhatsApp's renderer. `HtmlInline.Tag` from user-embedded HTML is also appended verbatim (`:268`). Unlike the Telegram converter, which HTML-encodes literals, WhatsApp's path has no escape.
- **Risk:** (1) User-supplied text reflected through the agent can alter WhatsApp formatting (low severity injection — no exec, just visual). (2) If an LLM returns `\*literal stars\*` markdown escapes, Markdig converts them to literal `*`, which WhatsApp re-interprets as bold. (3) `HtmlInline.Tag` passthrough was an explicit choice in the Telegram converter to *escape* raw HTML (`TelegramMarkdownConverter.cs:272`); in WhatsApp it's pass-through — the LLM can inject things like `<script>` that WhatsApp won't render but other consumers might.
- **Fix:** Escape literal `*`, `_`, `~`, backtick with a preceding `\` or a zero-width space before writing to `sb`. Mirror Telegram's HtmlInline treatment: skip or escape.

### Telegram webhook dispatcher uses `CancellationToken.None` (severity: low)

- **Location:** `TelegramWebhookEndpoints.cs:70-81`.
- **Issue:** `_ = Task.Run(async () => await handler.HandleUpdateAsync(sender, update, CancellationToken.None));` detaches the LLM call from host cancellation. On `ConnectionManager.StopAsync`, the handler continues even as the Telegram provider is torn down; the LLM completion runs against a possibly-disposed conversation store once DI shuts down.
- **Risk:** Late writes to the SQLite store (`AddMessage`, `UpdateChannelMessageId`) after container shutdown → exceptions logged, but otherwise benign. In containerized deployments, Kestrel's 30-s grace period lets detached Tasks run to completion; with slow LLMs, they'll be killed anyway.
- **Fix:** Pass `app.Lifetime.ApplicationStopping` or a token tracked by the provider so handlers cooperate with shutdown.

### KeepTypingAsync fires `SendChatAction` on cancellation (severity: low)

- **Location:** `TelegramMessageHandler.cs:224-236`.
- **Issue:** The keep-typing loop calls `await Task.Delay(4000, ct); await sender.SendTypingAsync(chatId, ct);` on `ct`. If the outer handler path finishes in less than 4 seconds (fast cached LLM response), the single `Task.Delay(4000, ct)` swallows the cancellation via `OperationCanceledException` catch (`:234`), the function exits cleanly — fine. But if the typing action itself is in flight when cancellation fires, the `catch (Exception)` at `:235` hides the specific `OperationCanceledException` pathway. Low risk; more about noisy fallback behavior.
- **Fix:** Narrow the second catch to exception types expected from transport failures.

### Draft consumer can stall on permanently-failing draft (severity: low)

- **Location:** `TelegramMessageHandler.cs:371-408`.
- **Issue:** When `SendDraftAsync` returns `Ok=false`, the code records `backoffUntil` but continues the loop. For a persistent 400 (e.g., malformed markdown, unsupported parse_mode — though drafts here send plain text), each iteration re-enters the "in backoff" branch, skips sending, and checks `done`. On the producer-done signal, the loop exits via `if (done) break;` — good. But if the producer never sends `done` (e.g., the LLM stream hangs), we keep looping at `DraftIntervalMs` forever. Cancellation via `ct` is the only out.
- **Risk:** Low — producer-side LLM timeouts cover this in practice. Worst case: log spam at warning level.
- **Fix:** Add a terminal-failure counter; after N consecutive failures, abandon draft mode and fall through to final send.

### TelegramBotClientSender leaks HttpClient on restart (severity: low)

- **Location:** `TelegramBotClientSender.cs:16-23`, `TelegramChannelProvider.cs:77, 143-162`.
- **Issue:** `_httpClient = new HttpClient { ... }` is created per provider instance and never disposed. `TelegramChannelProvider.StopAsync` doesn't dispose the sender. Since the connection lifecycle in the update flow is stop + start on every PUT (`ConnectionEndpoints.cs:98-120`), each update leaks one HTTP client (and its underlying socket pool).
- **Risk:** Socket exhaustion under heavy config-churn workloads — unlikely in practice, but the pattern is sloppy.
- **Fix:** Implement `IAsyncDisposable` on `TelegramBotClientSender` or share a single HttpClient (the Telegram.Bot library has its own; only the draft call needs raw HTTP — use a DI-managed `IHttpClientFactory` for that single call).

### Telegram allowlist dropped messages still dedup in WhatsApp (severity: low)

- **Location:** `WhatsAppMessageHandler.cs:67-73, 75-84`.
- **Issue:** `TryRecordMessage` records the ID *before* the gating check at `:76-84`. If the operator unlocks `AllowNewConversations` and the user re-sends, the new message has a new Baileys ID, so it'll be processed — fine. But if the bridge re-delivers the same message ID after reconnect (Baileys does replay unseen history — `syncFullHistory: false` at `baileys-bridge.js:121` mitigates this, but `messages.upsert` with `type=notify` can still fire twice for some reconnect cases), the dedup now rejects it. Net effect: "reject once, then silently ignore forever." Low in practice because Baileys' replay scope is bounded, but worth fixing.
- **Fix:** Move `TryRecordMessage` below the gating check so rejected messages don't poison dedup.

### WhatsApp logs text payload at Information level (severity: low — privacy)

- **Location:** `WhatsAppMessageHandler.cs:102`, `TelegramMessageHandler.cs:114`.
- **Issue:** `_logger?.LogInformation("Message from chat {ChatId}: {Text}", chatId, message.Text);` logs the full message body at Information — persisted to `logs/log-{date}.jsonl`, visible in the log explorer UI, and retained. Same for Telegram: `_logger?.LogInformation("Message from user {UserId} in chat {ChatId}: {Text}", ...);`.
- **Risk:** PII leakage into log files; user messages to a WhatsApp/Telegram agent are expected to be private.
- **Fix:** Log `message.Text.Length` instead of the body, or move to Debug. If the body is needed for triage, hash or truncate.

### QR pairing race between StartPairingAsync and HandleNodeEvent (severity: low)

- **Location:** `WhatsAppChannelProvider.cs:132-145, 252-263`.
- **Issue:** `StartPairingAsync` sets `_qrReady = new TaskCompletionSource<...>()` under `_lock`, then calls `StartNodeProcessAsync` outside the lock. `HandleNodeEvent` (the "qr" branch) runs from the stdout read loop on a separate Task; it reads `_qrReady`, sets the result, nulls it. If `GetQrAsync` races with `StartPairingAsync` on a fresh state, `GetQrAsync` may observe `_qrReady == null` (cleared by the qr event) and skip the await — that path uses `_latestQr` which is now set, so it's OK. But if two callers hit `GetQrAsync` concurrently, both may create two TCS's and only one gets signaled.
- **Risk:** Low — the pairing endpoint is single-user by nature. Visible as "QR waits timeout" for the losing caller.
- **Fix:** Guard all access to `_qrReady` under `_lock`; preserve a single TCS per pairing session.

### Lost updates between connection restart and Telegram re-delivery (severity: low)

- **Location:** `TelegramChannelProvider.cs:143-162`, CLAUDE.md (design decision: webhook stays registered).
- **Issue:** On `StopAsync` in webhook mode, the webhook remains registered. When the provider restarts with a new `_webhookSecret` (see the "Webhook secret regenerated" bug), Telegram's retries use the old secret — rejected at the validation step. Telegram gives up after some retries.
- **Risk:** Transient message loss across restarts. Mitigated if secret persistence is fixed.
- **Fix:** Fix the secret persistence bug; this one goes away.

### TelnyxChannel is a ghost (severity: medium — infrastructure hygiene)

- **Location:** `src/agent/OpenAgent.Channel.Telnyx/` (entire folder).
- **Issue:** The folder contains only `bin/Debug/net10.0/OpenAgent.Channel.Telnyx.dll` (from an earlier build) and `obj/` residue. There is no `.csproj`, no `.cs`, no factory, no endpoint. The solution file (`OpenAgent.sln`) does not reference Telnyx (grep confirmed). The CLAUDE.md and the review scope reference it, docs (`docs/references/telnyx-telephony-plugin-brief.md`) describe the intended design, but the implementation has been deleted or never checked in.
- **Risk:** (1) Confusing contributors — a project named `OpenAgent.Channel.Telnyx` exists on disk but does nothing. (2) Stale DLL in `bin/` can be picked up by probing and create shadowed-type diagnostics if a new Telnyx assembly is later added with the same name. (3) Deployment scripts that glob `bin/` may ship the dll even though no corresponding source is tracked.
- **Fix:** Delete the folder (`rm -rf src/agent/OpenAgent.Channel.Telnyx`). When Telnyx is ready to implement, scaffold fresh. (The review brief says "voice call webhooks need signature verification" — when built, Telnyx webhook handlers must verify the `Telnyx-Signature-Ed25519` + `Telnyx-Timestamp` headers against the public key, constant-time compare, timestamp window check — see the Telnyx call-events security docs.)

### Node bridge does not handle a bad JSON line from .NET (severity: low)

- **Location:** `baileys-bridge.js:228-262`.
- **Issue:** `const cmd = JSON.parse(line);` — if .NET sends a malformed line (shouldn't happen given serialization, but consider a TTY user piping the same bridge for debugging), the `catch` logs to stderr but the bridge keeps running. That's correct. One sharper concern: there's no validation of `cmd.chatId` or `cmd.text` types before calling `sock.sendMessage(cmd.chatId, { text: cmd.text })`. Baileys may throw on odd inputs, but the try/catch at `:259-261` swallows it. Low.
- **Fix:** Add a minimal shape guard; the current catch is sufficient.

### Node bridge lacks stderr backpressure visibility (severity: low)

- **Location:** `WhatsAppNodeProcess.cs:354-376`, `baileys-bridge.js` (all `console.error` calls).
- **Issue:** stderr is read line-by-line into the logger at Warning level. Under heavy Baileys logging (if `pino({ level: "silent" })` were relaxed), stderr could produce thousands of lines/second and flood Serilog. The brief asks "stdout deadlock if Node outputs > buffer size"; `ReadLineAsync` reads in a loop so no deadlock, but stderr-to-Warning is aggressive. Acceptable given current `pino` setting.
- **Fix:** No action needed while pino silent; revisit if log level changes.

## Smells

### Duplicated handler plumbing between Telegram and WhatsApp (severity: medium)

- **Location:** `TelegramMessageHandler.cs:87-170`, `WhatsAppMessageHandler.cs:57-177`.
- **Issue:** Conversation gating, `FindOrCreateChannelConversation`, provider resolution, `CompleteAsync` iteration, `AssistantMessageSaved` handling, chunk-and-send, `UpdateChannelMessageId` — all duplicated. Each handler is ~200 lines of near-identical orchestration with per-channel hooks for composing indicator and send.
- **Suggestion:** Extract a `ChannelPipeline` base class or composition: `Pipeline(ChannelName, IConversationStore, IConnectionStore, Resolver, AgentConfig)` with virtual methods `SendComposing`, `SendFinal(text)`, `PlatformPrefix(message)` for group attribution, `MaxLength`, `MarkdownConvert(text)`. Both handlers become ~30 lines of platform specifics.

### Platform-specific markdown converters could share a common framework (severity: low)

- **Location:** `TelegramMarkdownConverter.cs`, `WhatsAppMarkdownConverter.cs`.
- **Issue:** Both use Markdig, both walk the AST, both have nearly identical chunking logic (`ChunkMarkdown` vs. `ChunkText`). The only real difference is the "render" layer (tags vs. markers).
- **Suggestion:** A shared `MarkdownWalker` with a `Dialect` interface (returns open/close markers per element, handles literal escaping, handles link formatting). Chunking is pure string manipulation; extract into a shared utility.

### `CancellationToken.None` in fire-and-forget Task.Run paths (severity: medium)

- **Location:** `WhatsAppChannelProvider.cs:281-291` (message handling), `TelegramWebhookEndpoints.cs:74` (webhook dispatch).
- **Issue:** Both handler dispatchers `Task.Run(async () => { await ...HandleXxx(CancellationToken.None); })`. This ignores connection-level cancellation; on `StopAsync`, in-flight handlers keep running with their own LLM completions.
- **Suggestion:** Thread a provider-owned `CancellationTokenSource` (`_handlerCts`) through. Cancel on `StopAsync` to abort pending LLM calls cooperatively. Log counts of cancelled-in-flight on shutdown.

### Auto-lock logic implicit, scattered across handlers (severity: low)

- **Location:** `TelegramMessageHandler.cs:98-101`, `WhatsAppMessageHandler.cs:86-89`.
- **Issue:** The "flip `AllowNewConversations` to false on first chat" behavior is duplicated in each handler and tangled with gating. CLAUDE.md promotes it as a feature but it's not documented where it runs.
- **Suggestion:** Move into a shared `ConnectionGate.Admit(connection)` service. Test it once. Handlers call `if (!gate.Admit(existing, connection)) return;`.

### Magic numbers scattered across handlers (severity: low)

- **Location:** `TelegramMessageHandler.cs:19-28` (4096, 3, 300, retry delays), `WhatsAppMessageHandler.cs:16-19` (4096, 5000, 2500, 20 min), `WhatsAppChannelProvider.cs:239, 376, 429` (60 s ping, 2000 base/1.5x/30000 cap/10 attempts, 70 s stale).
- **Suggestion:** Centralize in `TelegramOptions` / `WhatsAppOptions` (with defaults) so operators can tune per-deployment. Document in `appsettings` section.

### Insufficient logging at connection transitions (severity: low)

- **Location:** `ConnectionManager.cs:72-92` (no log on early return when already running), `TelegramChannelProvider.cs:143-162` (webhook mode stop is a no-op logged at Info — fine), `WhatsAppChannelProvider.cs:92-110` (Unpaired vs. Connected branching).
- **Issue:** State transitions log at info level inconsistently. The state changes in `HandleNodeEvent` (`:247-343`) set `_state` under `_lock` but logs are outside; the ordering is readable but debugging a wedged state requires correlating timestamps. `_lastError` is set without a log at `:336`.
- **Suggestion:** Structured state-transition logging: `Info("WhatsApp[{Id}] {From} -> {To} (reason={Reason})", ...)`.

### FileConnectionStore uses `SemaphoreSlim.Wait()` on sync methods (severity: low)

- **Location:** `FileConnectionStore.cs:30-39, 43-53, 58-76, 81-97`.
- **Issue:** `LoadAll`, `Load`, `Save`, `Delete` are all synchronous. Called from handler hot paths (e.g., every message reads the connection on a webhook dispatcher thread). `SemaphoreSlim.Wait()` blocks a thread-pool thread; the underlying `File.WriteAllBytes`/`ReadAllBytes` are also blocking.
- **Suggestion:** Either make the interface async throughout (`IConnectionStore.SaveAsync`, etc.) or at least cache in-memory with a debounced disk flush. Under heavy channel traffic, every message opens and reads the entire connections file.

### `TelegramChannelProvider` stores concrete `TelegramBotClientSender` (severity: low)

- **Location:** `TelegramChannelProvider.cs:27, 170`.
- **Issue:** Field is the concrete class (`TelegramBotClientSender?`), not the interface (`ITelegramSender?`). `CreateSender()` returns the abstraction but the field binding is tight.
- **Suggestion:** Type the field as `ITelegramSender?`; removes coupling, makes dependency substitution straightforward in tests.

### Node bridge path resolution duplicated (severity: low)

- **Location:** `WhatsAppChannelProvider.cs:228`, `WhatsAppNodeProcess.cs:177`.
- **Issue:** Both `WhatsAppChannelProvider.StartNodeProcessAsync` and `WhatsAppNodeProcess.StartAsync` independently compute `Path.Combine(AppContext.BaseDirectory, "node", "baileys-bridge.js")`. The provider passes the path to the process constructor, which then ignores it and recomputes.
- **Suggestion:** Pass once; use the stored field. See bug #4.

### JSON parsing in factories is hand-rolled (severity: low)

- **Location:** `TelegramChannelProviderFactory.cs:51-106`, `WhatsAppChannelProviderFactory.cs:57-80`.
- **Issue:** Both deserialize `connection.Config` field-by-field, with duplicated "string or array" fallbacks. Comment at `TelegramChannelProviderFactory.cs:52-53` explains it — the dynamic form sends comma-separated strings. But this reasoning applies to many channel types equally.
- **Suggestion:** Write a `ChannelConfigReader` helper: `TryGetString(key)`, `TryGetLongList(key)`, `TryGetStringList(key)`, `TryGetBool(key)` with the "or-comma-separated-string" semantics baked in. Factories become small.

## Open Questions

1. **Is Telnyx intentionally removed?** The folder hangs around with stale build output. Should it be cleaned up or is there pending work in a branch?
2. **Should `IOutboundSender.SendMessageAsync` handle chunking?** Currently the WhatsApp implementation (`WhatsAppChannelProvider.cs:205-211`) sends the whole text in a single `FormatSendCommand` — no chunking at 4096 chars, unlike the inbound reply path. Scheduled task outputs > 4096 chars will be rejected by WhatsApp.
3. **What is the intended flow for the `AllowedUserIds` / `AllowedChatIds` lists?** Delete, or wire into the handlers? If wire-in, should they ANDed with `AllowNewConversations` (must be allowed AND new allowed) or ORed?
4. **Why is `AllowNewConversations` auto-locked to false on first conversation?** The current behavior silently drops all subsequent first-contacts — surprising UX. Is this a temporary measure until a proper allowlist exists?
5. **Telegram `conversationId` in `Connection` is unused for channels.** `CreateConnectionRequest.ConversationId` (`ConnectionEndpoints.cs:213-214`) is persisted but the handlers derive their conversations via `FindOrCreateChannelConversation` — what was `Connection.ConversationId` supposed to bind?
6. **Should the WhatsApp Node bridge auto-install its dependencies?** CLAUDE.md says `npm ci --omit=dev` must be run manually after publish. In the Windows-service pre-install checks (`PreInstallChecks`), there's a `node --version` check but no npm install step. Would operators benefit from automation?
7. **Any reason the Telegram polling path uses `ReceiverOptions.AllowedUpdates = [Message]` but the webhook path accepts every update?** Webhook-side `SetWebhook` doesn't pass `allowed_updates` — channel-post, edited-message, callback-query, etc. all flow in and get filtered downstream.

## Files reviewed

- `src/agent/OpenAgent.Contracts/IChannelProvider.cs`
- `src/agent/OpenAgent.Contracts/IChannelProviderFactory.cs`
- `src/agent/OpenAgent.Contracts/IOutboundSender.cs`
- `src/agent/OpenAgent.Contracts/IConversationStore.cs` (for handler call-sites)
- `src/agent/OpenAgent/ConnectionManager.cs`
- `src/agent/OpenAgent.ConfigStore.File/FileConnectionStore.cs`
- `src/agent/OpenAgent.Models/Connections/Connection.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProviderFactory.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramBotClientSender.cs`
- `src/agent/OpenAgent.Channel.Telegram/ITelegramSender.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramOptions.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramMarkdownConverter.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramWebhookEndpoints.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProviderFactory.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMarkdownConverter.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppEndpoints.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppOptions.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/IWhatsAppSender.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js`
- `src/agent/OpenAgent.Channel.WhatsApp/node/package.json`
- `src/agent/OpenAgent.Api/Endpoints/ConnectionEndpoints.cs`
- `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs` (schema + FindOrCreate only)
- `src/agent/OpenAgent.ScheduledTasks/DeliveryRouter.cs` (IOutboundSender consumer)
- `src/agent/OpenAgent.Channel.Telnyx/` (empty — noted as finding)
