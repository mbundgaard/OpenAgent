# OpenAgent Code Review — 2026-04-23

Full-codebase review across eight independent domains, 218 C# files (~24 KLOC) in 29 projects, plus a 43-file React 19 UI. This is the **synthesis** — each finding here links to its domain file for full rationale, file:line evidence, and proposed fixes.

## How to read this

| # | Domain | File | Top-line |
|---|---|---|---|
| 01 | Security & Auth | [01-security.md](./01-security.md) | Path-traversal class bug × 9, unmasked connection secrets, SSRF via redirect |
| 02 | Architecture & Core | [02-architecture.md](./02-architecture.md) | SystemPromptBuilder race, keyed-singleton DI footguns, `Program.cs` growing |
| 03 | LLM Providers | [03-llm-providers.md](./03-llm-providers.md) | HttpClient lifecycle, voice session races, Gemini reconnect amnesia |
| 04 | Channels | [04-channels.md](./04-channels.md) | Advertised access control is dead, sequential/blocking shutdown, Telnyx is a ghost |
| 05 | Memory & Storage | [05-memory-storage.md](./05-memory-storage.md) | FTS5 triggers missing, non-atomic index/move, TryAddColumn silently hides schema errors |
| 06 | Tools & Skills | [06-tools-skills.md](./06-tools-skills.md) | Same prefix bug + tool-name dedup + lost-update race on ActiveSkills |
| 07 | Deployment & Terminal | [07-deployment-terminal.md](./07-deployment-terminal.md) | Remote SYSTEM shell, installer ignores exit codes, wwwroot churn |
| 08 | Frontend & Tests | [08-frontend-tests.md](./08-frontend-tests.md) | Token in sessionStorage + `?api_key=` in every WS URL; zero frontend tests |

**Counts:** ~100 bugs + ~70 smells logged across the eight files. The reviewers' top-5 lists were deliberately conservative; many medium-sev items in the domain files are worth reading if you touch that area.

---

## TL;DR

The architecture is fundamentally sound. The **provider pattern**, **CompletionEvent** as the single streaming event type, **Contracts**-based cross-project seams, and the **IChannelProviderFactory** metadata-driven form system are the shape a production-quality agent platform should have. Keyed DI + `Func<string, TProvider>` resolvers make provider swaps genuinely live.

The problems cluster in three buckets:

1. **Blast-radius mistakes around the newer features.** The Terminal WebSocket, running as `LocalSystem` in service mode with `?api_key=` query auth and no Origin check, is effectively a remote SYSTEM shell behind a leaked key. That's the single highest severity item in the codebase.
2. **A family of path/scope check bugs that repeat.** Every tool that needs "is this path inside my root?" uses `fullPath.StartsWith(root)` with no separator guard. It's broken in nine places and it's the same fix each time — a helper in `OpenAgent.Contracts`.
3. **A cluster of mid-severity concurrency bugs** that are individually survivable but compound: keyed singletons disposing `HttpClient` on `Configure()` while requests stream; check-then-act races in `ConnectionManager.StartConnectionAsync`, `WebSocketRegistry.Register`, and the conversation store's `FindOrCreateChannelConversation`; lost-update races on `Conversation.ActiveSkills`/`Intention`/`Model` under parallel tool calls; fire-and-forget `Task.Run(..., CancellationToken.None)` from webhook dispatchers, the compaction path, and voice sessions.

There are also **two substantial dead-code hygiene issues** worth cleaning up before they mislead contributors: the `OpenAgent.Channel.Telnyx` project has no source (only `bin/`/`obj/` residue and no `.csproj`) despite being referenced in CLAUDE.md and the review brief; `OpenAgent.Voice.OpenAI` is an empty folder. Telegram/WhatsApp both parse `AllowedUserIds`/`AllowedChatIds` options but no handler ever reads them.

Test coverage is uneven. The Sqlite store, Telegram handler, memory index, and installer have strong integration tests. The admin endpoints, file-explorer endpoints (the path-traversal attack surface), connection endpoints, `AgentLogic`, `SystemPromptBuilder`, `ShellExecTool`, and the entire React UI have **zero** tests.

---

## Critical — fix before any remote deployment

### C-1 Terminal WebSocket = remote SYSTEM

`/ws/terminal/{sessionId}` bridges raw bytes to `bash -i` / `cmd /Q`. Auth accepts `X-Api-Key` header *or* `?api_key=` query. No Origin check, no CSRF mitigation. Windows service mode runs as `LocalSystem`. The `--install --open-firewall-port` verb opens TCP 8080 on *all* profiles (public included). API-key comparison is non-constant-time. Token lives in `sessionStorage` (copied out of `window.location.hash`) so any supply-chain XSS exfiltrates it.

**The combination is remote-interactive SYSTEM on any leaked or guessed key.**

Fix (layered, all of them): Origin allow-list on the WS endpoint, drop `?api_key=` in favour of `Sec-WebSocket-Protocol` subprotocol tokens, move service account to `NT SERVICE\OpenAgent` virtual account via `sc create obj="NT SERVICE\OpenAgent"`, make the terminal endpoint opt-in in config (default off in service mode), and fix the constant-time compare.

*See [07-deployment-terminal.md §Terminal WebSocket is a remote shell](./07-deployment-terminal.md) + [01-security.md §API key comparison is not constant-time](./01-security.md).*

### C-2 Path-scope prefix check accepts sibling directories (9 call sites)

Every `StartsWith(basePath)` check in the codebase is missing a trailing-separator guard. For `basePath = "C:\data\agent"`, the path `../agent-evil/x.txt` resolves to `C:\data\agent-evil\x.txt`, which *does* start with `C:\data\agent` and passes the gate.

Affected sites, all high-severity:

- `FileReadTool.cs:39`, `FileWriteTool.cs:40`, `FileAppendTool.cs:39`, `FileEditTool.cs:43` — the LLM's general file tools
- `FileExplorerEndpoints.cs:264` (`ResolveSafePath`) — the UI's file browser
- `LogEndpoints.cs:74` — the `/api/logs/{filename}` reader
- `ShellExecTool.cs:73` — the shell tool's cwd escape check
- `SkillToolHandler.cs:249` — `activate_skill_resource`, which does explicitly block `../` but then falls into this prefix bug

Impact: read/write/edit/execute-from any sibling directory of the data root on a Windows install, or `/home/data-restore/` on the Azure container. The file-tools `IConfigurable` plus the log endpoint can be chained by anyone with the API key.

**Fix once:** extract a `PathScope.IsInside(root, candidate)` helper into `OpenAgent.Contracts` using `Path.GetRelativePath(root, resolved)` + reject results starting with `..`. Replace all nine sites.

*See [01-security.md §Path traversal via prefix-only root check](./01-security.md) and [06-tools-skills.md §Path prefix check accepts sibling directories](./06-tools-skills.md).*

### C-3 WebFetch follows redirects with no SSRF re-validation

`WebFetchToolHandler.cs:14` constructs `new HttpClient { Timeout = ... }` — default handler has `AllowAutoRedirect = true`. `UrlValidator.ValidateWithDnsAsync` validates only the initial URL. A public attacker-controlled 302 → `http://169.254.169.254/latest/meta-data/iam/...` (AWS IMDS), `http://localhost:8080/api/memory-index/run`, or any RFC1918 host is followed without re-validation. Output returns to the LLM as markdown, so internal-service payloads exfiltrate through the model.

CLAUDE.md marks SSRF issue #7 as closed with "DNS rebinding accepted as risk." The redirect channel is a separate bypass and was not covered.

**Fix:** `new SocketsHttpHandler { AllowAutoRedirect = false }`, handle redirects in `WebFetchTool` with `ValidateWithDnsAsync` on each `Location` header, cap at 5 hops. A bonus: setting `SocketsHttpHandler.ConnectCallback` closes the DNS-rebinding TOCTOU at the same time.

*See [01-security.md §SSRF via HTTP redirect](./01-security.md) and [06-tools-skills.md §WebFetch follows redirects with no SSRF re-validation](./06-tools-skills.md).*

---

## High severity — fix this sprint

Grouped by theme. Each item links to its domain file for the evidence and fix.

### Secrets & auth hardening

- **Connection configs returned unmasked.** `GET /api/connections` ships `botToken` / WhatsApp creds verbatim; the admin endpoint's secret-masking loop was never applied to connections. *01-security.md.*
- **API key compared non-constant-time** in the main handler. The Telegram webhook path already uses `CryptographicOperations.FixedTimeEquals` — same call, just not applied to `ApiKeyAuthenticationHandler.cs:40`. *01-security.md.*
- **Gemini API key pasted into WS URL query string**; `ConnectAsync` exception messages then leak it into logs. All other voice providers use headers. *01-security.md.*
- **Token copied to `sessionStorage` after URL-hash handoff** — undoes the whole point of the hash-based boot ritual. Every dep (`react-markdown`, `xterm`, `@xterm/*`, `qrcode`, ...) can read it. Keep the token in a module-scoped JS variable. *08-frontend-tests.md.*
- **`?api_key=<fullKey>` on every WebSocket URL** lands in Kestrel/IIS/Azure App Service access logs. Use `Sec-WebSocket-Protocol` subprotocol tokens. *08-frontend-tests.md.*

### Dead code masquerading as a feature

- **`TelegramOptions.AllowedUserIds` / `WhatsAppOptions.AllowedChatIds` are never read.** The only live gate is `AllowNewConversations` (a one-shot admission boolean). Tests pass because they don't exercise the rejection path. Either wire the check or delete the fields and update CLAUDE.md. *04-channels.md.*
- **`OpenAgent.Channel.Telnyx` is a ghost project** — no `.csproj`, no source, only stale `bin/obj/`. Not referenced by the solution. Delete or commit source. *04-channels.md.*
- **`OpenAgent.Voice.OpenAI` is an empty folder.** Delete. *03-llm-providers.md.*
- **`AgentConfig.MainConversationId` is persisted and editable but nothing reads it.** *02-architecture.md.*
- **`WhatsAppNodeProcess._scriptPath` is constructor-injected, stored, and never used** — `StartAsync` recomputes the path from `AppContext.BaseDirectory`. *04-channels.md.*

### Concurrency bugs

- **SystemPromptBuilder singleton mutates a non-thread-safe `Dictionary` during admin reload**, while request threads read it. Replace with `ConcurrentDictionary` or swap the field atomically. *02-architecture.md.*
- **`ConnectionManager.StartConnectionAsync` is check-then-act** — two concurrent starts leak a provider each (duplicate Telegram pollers, zombie Baileys Node). Use `GetOrAdd` with a `SemaphoreSlim` per id. *02-architecture.md + 04-channels.md.*
- **`WebSocketRegistry.Register` silently overwrites** an existing entry without closing the prior socket. Mirror the pattern in `WebSocketTerminalEndpoints.cs:51-56`. *02-architecture.md.*
- **`ActiveBridges` finally-block evicts the wrong registration** — connection A's `finally` removes connection B's entry, so two consumers race on the same PTY output channel. Use `ICollection<KVP>.Remove((sid, myCts))` overload. *07-deployment-terminal.md.*
- **Terminal sessions never closed on WebSocket drop.** The `finally` closes the socket but leaves the bash/cmd child and its IO reader alive. Four drops = feature dead until host restart. *07-deployment-terminal.md.*
- **`activate_skill` / `set_intention` / `set_model` / `deactivate_skill` race on `store.Update`** — classic read-modify-write. Anthropic providers can issue parallel tool calls. Lost-update silently drops skill activations. Per-conversation `SemaphoreSlim` at the tool layer. *06-tools-skills.md.*
- **No tool-name uniqueness check** in `AgentLogic._allTools`. Two handlers registering the same name → LLM sees both definitions in the schema, only one runs. Throw at startup. *06-tools-skills.md.*
- **`FindOrCreateChannelConversation` has no UNIQUE index** on `(ChannelType, ConnectionId, ChannelChatId)`. Telegram webhook dispatcher fires in parallel → duplicate conversations for the same chat. *04-channels.md.*
- **Both text providers dispose `_httpClient` on `Configure()`** while keyed singletons are mid-stream → `ObjectDisposedException` on in-flight completions during any admin save. Use `IHttpClientFactory` or a reader-writer lock. *03-llm-providers.md.*
- **`AzureOpenAiVoiceSession` fires tool tasks via untracked `Task.Run`** — dispose races with live `SendAsync` on a disposed socket. Grok already fixed this with `ConcurrentDictionary<Task, byte>` and `await WhenAll` in dispose. Port the pattern to Azure voice and Gemini. *03-llm-providers.md.*
- **`GeminiLiveVoiceSession.ReconnectAsync` swaps `_ws` without `_sendLock`.** Concurrent `SendMessageAsync` reads the stale disposed socket. Gemini reconnects every 13 minutes by design. *03-llm-providers.md.*
- **Scheduled-task tick can re-select an in-flight task** because `NextRunAt` isn't cleared at dispatch time. A 30s LLM call causes the same task to fire twice. *06-tools-skills.md.*

### Reliability bugs

- **`TryAddColumn` swallows every `SqliteException`**, not just duplicate-column errors. Real schema errors are silently accepted; next INSERT throws deep in request handling with no root-cause signal. *05-memory-storage.md.*
- **FTS5 external-content mirror has no DELETE/UPDATE triggers.** Dormant today because nothing deletes chunks, latent for any future "forget" feature or rebuild tool. Index will silently corrupt. *05-memory-storage.md.*
- **`FileConfigStore.Save` has no lock and no tmp-rename.** Concurrent writes torn-write each other; a crash mid-write leaves an empty file that bricks startup on next read. The parallel `FileConnectionStore` already has both, so this is just missing diligence. *05-memory-storage.md.*
- **Chunks-committed / source-file-moved is not atomic.** A crash between `InsertChunks` and `MoveToBackup` leaves the content in both the prompt AND the index. *05-memory-storage.md.*
- **FTS5 MATCH queries unvalidated.** The LLM writes `"what did Alice say?"` into `search_memory`, SQLite throws on the colon/quote, the tool returns a server error, the LLM loses trust and stops calling the feature. *05-memory-storage.md.*
- **Gemini voice reconnect loses all conversation history.** `SendSetupAsync` never replays prior turns. 15-min voice sessions become 13-min amnesia. *03-llm-providers.md.*
- **Grok voice has no `InputAudioTranscription`** — user speech is never persisted to conversation history. Half-deaf record. *03-llm-providers.md.*
- **Azure text HttpClient has no `Timeout`.** A stalled upstream hangs a thread forever. Anthropic sets 5 min, Azure sets nothing. *03-llm-providers.md.*
- **SSE parsing has no `JsonException` guard** on either text provider. One malformed chunk (content-filter response shape, proxy-injected comment) aborts the turn. *03-llm-providers.md.*
- **Tool-result content shipped to LLM with no size cap.** A runaway `shell_exec` pumps GBs into a single `tool_result`; Anthropic 200K context dies in two rounds and Azure bills the input tokens on the 400. *03-llm-providers.md.*
- **Missed scheduled-task replay blocks `StartAsync`** with sequential 2s delays per task. 30 overdue tasks = 60s hosted-service startup block. Fire-and-forget it. *06-tools-skills.md.*
- **`sc.exe` / `netsh` exit codes silently ignored** across every installer verb. `--install` prints success on failed `sc create`. *07-deployment-terminal.md.*
- **`ExtractEmbeddedWwwroot` deletes-and-recreates on every cold start.** Concurrent-process race, disk churn, 404 window during service restart. Move to versioned-folder + junction swap, or hash + skip. *07-deployment-terminal.md.*
- **Telegram webhook secret regenerated on every `StartAsync` and never persisted.** In-flight updates are rejected across any config-triggered restart. *04-channels.md.*
- **`WhatsAppNodeProcess.WriteAsync` silently drops commands** via `TryWrite` after the channel is completed. `IOutboundSender.SendMessageAsync` reports success; scheduled task delivery never arrives. *04-channels.md.*
- **`ConnectionManager.StopAsync` is sequential and blocking** — each WhatsApp connection takes up to 5s (`Process.WaitForExit(TimeSpan)`) + 2s reader join. With three connections, host shutdown misses Azure's 30s grace. Parallelize. *04-channels.md.*
- **Scheduled-task body read unbounded** from `/api/scheduled-tasks/{id}/trigger` (30 MB cap from Kestrel). Body string-interpolated into the prompt — attacker can include `</webhook_context>` to break out. *01-security.md + 06-tools-skills.md.*

---

## Medium — broad quality-of-life and defense in depth

Full list in the domain files. Highlights that cut across several files:

- **WebFetch response body read before the size cap applies** — `ReadAsStringAsync` materialises the full body, then trims. OOM on a hostile fetch target. Stream-copy into a capped buffer instead.
- **Secrets persisted with default file mode** (0644 on Linux). `File.SetUnixFileMode` on write.
- **No rate limiting** on tool execution, auth failures, or webhook endpoints. `AddRateLimiter` with per-IP windows.
- **No CORS policy explicitly configured.** Default blocks preflight so it's fine today, but any future cookie-auth migration turns the silence into a trap.
- **Shell runs with service privileges, no sandbox.** `LocalSystem` + `shell_exec` = one prompt-injection from RCE. Document and move to `NT SERVICE\OpenAgent`.
- **HuggingFace model downloads have no checksum and track `main`** — silent supply-chain ingress.
- **Hand-rolled Azure OpenAI / Anthropic wire models duplicate the official SDKs.** Maintenance burden every API rev.
- **Two parallel chat UIs** (`TextApp` / `VoiceApp` inline WS vs the newer `ChatApp` + hooks). Delete the duplicates.
- **Anthropic `user-agent: claude-cli/2.1.91` and `anthropic-beta:` constants are hardcoded.** When Anthropic rotates the allowed set, the provider 429s with no admin path.
- **WhatsApp markdown converter doesn't escape `*`, `_`, `~`, backtick, HtmlInline.** LLM responses that quote `a*b_c` re-format in WhatsApp's UI.

---

## Cross-cutting themes — fix the pattern, not the instance

Eleven patterns the reviewers independently flagged from different domains. Each one, if addressed as a pattern, closes multiple issues at once.

1. **`PathScope.IsInside(root, candidate)` in Contracts** → closes C-2's 9 sites and prevents the next copy-paste regression.
2. **Keyed-singleton-with-mutable-`HttpClient`.** Move text providers to `IHttpClientFactory`; define an `ILifecycleConfigurable` shape where `Configure()` returns a new handle instead of mutating.
3. **Check-then-act → `GetOrAdd` with `Lazy<Task<T>>`.** `ConnectionManager`, `VoiceSessionManager`, `WebSocketRegistry`, `ActiveBridges`, `FindOrCreateChannelConversation` all benefit. Add a single helper.
4. **`Task.Run(..., CancellationToken.None)` from handlers.** TelegramWebhookEndpoints, WhatsAppChannelProvider, SqliteConversationStore.TryStartCompaction, Azure voice tool dispatch, Gemini tool dispatch. Thread a provider-owned `CancellationTokenSource` cancelled on dispose; drain in-flight work on shutdown.
5. **Per-conversation mutation lock.** `activate_skill` / `deactivate_skill` / `set_intention` / `set_model` / `clear_intention` / skill count cap — all lost-update-prone. One `ConcurrentDictionary<string, SemaphoreSlim>` in a `ConversationMutator` helper.
6. **Tool-result size cap.** Shell, file reads, web fetches, scheduled-task bodies. One config-driven cap at ingestion — don't fix each call site.
7. **Exit-code discipline for shelled-out tools.** `sc.exe`, `netsh`, `cmd /c mklink`, `npm`, `node`. Wrap in a single `RunOrThrow(verb, args)` helper with opt-in best-effort mode.
8. **Masked-secrets helper.** `AdminEndpoints` has the pattern; `ConnectionEndpoints` doesn't. Extract a `SecretMasker.Mask(configFields, jsonElement)`.
9. **`StoredToolCall` typed model** shared across text + voice providers. Today each provider serialises anonymous objects that happen to match; one rename breaks silently.
10. **Markdown-dialect framework.** Telegram + WhatsApp converters are ~80% identical AST walks. Extract a `MarkdownWalker` + `IDialect` with `Open/Close(element)`, `EscapeLiteral(text)`, etc.
11. **Text-provider lifecycle base.** Both providers re-implement the same "persist user → round loop with cap → stream SSE → accumulate → detect tool calls → persist → execute → re-loop → final persist → re-fetch conversation" sequence. A `TextProviderBase` with abstract `StreamRoundAsync(request, ct)` removes ~500 LOC of duplication and makes bug fixes propagate.

---

## Strengths worth preserving

Every reviewer called out things the codebase gets right. A partial list:

- **CompletionEvent as a universal streaming type** across REST, WebSocket, Telegram, WhatsApp, scheduled tasks. Protect this invariant.
- **IAgentLogic as injected context, not orchestrator.** Providers call the shots. This keeps providers replaceable and tests cheap.
- **Contracts-based cross-project seams.** `OpenAgent.Contracts` only depends on `OpenAgent.Models`. When Api needs a host type, you extract an interface. Keep the discipline.
- **Keyed DI + `Func<string, TProvider>` resolvers** for live provider switching without restart. The two endpoint files that bypass this (calling `GetRequiredKeyedService` directly) are outliers and should be fixed, not the pattern.
- **Telegram webhook constant-time compare** (`CryptographicOperations.FixedTimeEquals`). Just apply the same call to the main auth handler.
- **Skill persistence on `Conversation.ActiveSkills`** — compaction-proof by design. Resources as ephemeral tool results — strip-safe by design. The progressive-disclosure architecture matches the agentskills.io spec.
- **ONNX embedding providers with atomic download**: `.tmp` + `File.Move` rename, `SemaphoreSlim` around first use, provider+model stamped on every chunk so cosine never crosses vector spaces, `BinaryPrimitives` endian-pinned BLOB storage.
- **MemoryIndex serializes concurrent runs** with `_runLock` and has a regression test for it.
- **Scheduler runs LLM work outside the lock.** Classic mistake to do it the other way.
- **`FileEditTool` requires unique `old_text`** and rejects zero-op edits — avoids the classic "edit applied to the wrong place" LLM tool failure.
- **Bootstrap is genuinely idempotent** with explicit handling of missing / wrong-target / regular-directory states.
- **Installer has a thin `ISystemCommandRunner` abstraction** with a fake that lets every step be verified.
- **PTY session allocation serialises around `posix_openpt` + `grantpt` + `unlockpt` + `ptsname`** because `ptsname` returns a static buffer — a subtle thing done right.
- **React UI has `history.replaceState` hash-stripping** after token extraction — the intent is exactly right, the `sessionStorage` copy just has to go.
- **`apiFetch` wrapper** centralises auth so adding CSRF/retry/refresh is a one-file change.
- **Strict Mode on.** The team has already patched the obvious double-invoke issues.
- **Tests for the Telegram handler, Sqlite store, memory index, and installer are thorough** and exercise real scenarios. Propagate the style to the untested endpoints.

---

## Action plan (prioritized)

### This week (security)

1. C-1 Terminal: add Origin check + make opt-in + move to `NT SERVICE\OpenAgent` + kill `?api_key=` query auth
2. C-2 Path scope: extract `PathScope.IsInside(root, candidate)` helper, fix nine sites, add test per site
3. C-3 WebFetch: `AllowAutoRedirect = false`, re-validate on each `Location`
4. `ApiKeyAuthenticationHandler.cs:40` → `CryptographicOperations.FixedTimeEquals`
5. `ConnectionEndpoints.cs:160-170` → apply `AdminEndpoints`-style secret masking
6. Gemini voice — move API key from URL to `ClientWebSocketOptions.SetRequestHeader`
7. Frontend — remove `sessionStorage` copy; switch WS auth to `Sec-WebSocket-Protocol` subprotocol

### Next sprint (reliability)

8. Remove dead code: Telnyx, Voice.OpenAI, `AllowedUserIds`/`AllowedChatIds`, `MainConversationId`, `WhatsAppNodeProcess._scriptPath` — or wire them up
9. `SystemPromptBuilder` + keyed-singleton `Configure()` lifecycle (switch to `IHttpClientFactory` for text providers)
10. `ConnectionManager.StartConnectionAsync` + `WebSocketRegistry.Register` + `ActiveBridges` finally — CAS-style patterns
11. Terminal session closure on WS drop (one-line fix, high impact)
12. Per-conversation mutation lock for skill/intention/model tools
13. Tool-name uniqueness check at `AgentLogic` construction
14. `TryAddColumn` — rethrow on non-duplicate errors
15. `FileConfigStore.Save` — `SemaphoreSlim` + tmp+rename
16. Scheduled-task `NextRunAt` clear at dispatch
17. `sc.exe` / `netsh` `RunOrThrow` helper
18. `ExtractEmbeddedWwwroot` — extract-to-temp + move (or hash + skip)
19. Gemini reconnect — replay conversation history on setup
20. Grok voice — add `InputAudioTranscription`
21. Azure text — `HttpClient.Timeout` (and all the SSE `JsonException` guards across both providers)
22. Tool-result size cap
23. FTS5 triggers + non-atomic index/move fix
24. Scheduled-task body size cap + escape for `</webhook_context>`

### Next quarter (structural)

25. Cross-cutting themes #2 (HttpClient lifecycle), #4 (Task.Run handlers), #9 (StoredToolCall), #10 (Markdown framework), #11 (TextProviderBase)
26. Test suite expansion for admin / file-explorer / connection / WebSocket-text endpoints; the first path-traversal integration test
27. Tiny frontend test suite: `token.ts`, `useWindowManager.ts`, `MarkdownViewer` frontmatter parser
28. Rate limiter
29. Consider `IHttpClientFactory` migration across all providers
30. Consider replacing hand-rolled wire models with `Azure.AI.OpenAI` + `Anthropic.SDK` (keeping auth customisation)

---

## Outstanding design questions (raised across reviews)

- **Single shared API key vs per-role keys.** The key grants full file + shell access. Do we want read-only vs shell-enabled keys, or should pluggable-auth graduate to Entra ID / GitHub OAuth before any remote user gains access?
- **`ConversationType` doc vs code.** CLAUDE.md promises `Text` / `Voice` / `ScheduledTask` / `WebHook`; enum has only `Text` / `Voice`. Align one way.
- **Compaction lifecycle.** Today `TryStartCompaction` fires from inside a sync `Update()` path via `Task.Run`. Should it be its own `IHostedService` with a drain-on-shutdown semantic? (CLAUDE.md already hints at a future "system jobs" abstraction that would host index + digest + background together.)
- **Token-in-URL-hash ritual.** Keep the bookmark-friendly handoff but kill the `sessionStorage` copy? Or convert to a one-shot `/auth/exchange` endpoint that swaps the fragment for an `HttpOnly` cookie?
- **Terminal in service mode.** Is the terminal meant to be reachable from anywhere but the installing user's browser? If not, gate it behind a `TerminalEnabled` config default-false in service mode.
- **Scheduler timezone vs system-prompt timezone.** Scheduler defaults UTC, system prompt injects Europe/Copenhagen. "Every weekday at 9am" (no TZ) fires at 10am local. Default the scheduler to match what the prompt displays?
- **`AllowNewConversations` auto-lock** — current behavior silently drops all subsequent first-contact messages after the first conversation. Documented as "auto-lock after first", surprising UX. Keep as-is, document, or replace with an admitted-chats allowlist?
