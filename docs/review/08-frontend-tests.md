# Frontend & Tests Review — 2026-04-23

## TL;DR
The React 19 + TS frontend is compact, well-structured around a small windowed desktop, and avoids most of the obvious client-side footguns (no `dangerouslySetInnerHTML`, markdown is safe-by-default, Strict Mode is on). The largest concerns are token handling (API key copied into `sessionStorage` and passed as a `?api_key=` query parameter on every WebSocket URL — ends up in server access logs), auth leakage in the dev proxy path, and several lifecycle bugs in the voice/terminal hooks that React Strict Mode double-invoke will amplify. On tests: 255 facts across 42 files give reasonable integration coverage for the Telegram handler, Sqlite store, memory index, and installer, but the host authoritative test path leans on a shared global `/home/data` env var and a hard-coded `dev-api-key-change-me`. Several service layers (endpoints under `/api/admin`, `/api/tools`, `/api/files*`, `/api/logs`, `/api/memory-index`, `/api/connections`, ConnectionManager, AgentLogic, DataDirectoryBootstrap-in-Linux, system prompt assembly end-to-end) have no direct tests. There are zero frontend tests.

## Strengths
- **Token isolation**: `auth/token.ts` strips the hash from the URL via `history.replaceState` after read, so the URL-bar doesn't keep the secret visible. This is a real defense against shoulder-surfing and sync to history providers.
- **apiFetch wrapper** in `src/web/src/auth/api.ts` is a genuine shared client — every REST call in the app goes through it, so adding CSRF / refresh / retry later is a one-file change.
- **Fakes folder** in tests is well-scoped: `FakeTelegramSender`, `InMemoryConversationStore`, `StreamingTextProvider`, `ThrowingTextProvider`, `FakeEmbeddingProvider` are all small, focused, and implement the real interfaces. They drift protection-ably (compile-time).
- **Integration tests** use `WebApplicationFactory<Program>` cleanly with targeted `RemoveAll` + `AddKeyedSingleton` overrides (ChatEndpointTests, VoiceWebSocketTests, ScheduledTaskEndpointTests) — realistic end-to-end coverage.
- **SqliteConversationStoreTests** exercise compaction, modality, type transitions, and row-id semantics — the store is genuinely well-tested.
- **TelegramMessageHandlerTests** (13 facts) cover allowlist, html fallback, streaming/batch modes, and draft failure — probably the best-tested unit in the solution.
- **Strict Mode** is enabled (`main.tsx:7`) and the teams have already patched around it at `TerminalApp.tsx:70-167` (the `disposed` flag), showing they tested double-mount.
- **useReducer-based `useWindowManager`** is pure, deterministic, and easily unit-testable if someone chooses to start; good architecture.

---

## Frontend: Bugs

### Token copied to sessionStorage on first load (severity: medium)
- **Location:** `src/web/src/auth/token.ts:19`
- **Issue:** The API key is written to `sessionStorage['openagent-token']` after being extracted from the URL. sessionStorage persists for the lifetime of the tab (including across reloads and discarded → restored tabs in some browsers) and is accessible to every script running in that origin, including any compromised dependency (`react-markdown`, `qrcode`, `react-rnd`, `xterm`, `@xterm/*`, `remark-gfm`). With 10+ transitive deps, one supply-chain compromise exfiltrates the admin API key.
- **Risk:** Single XSS/supply-chain compromise = full API key theft. The "hand off via URL hash" design is specifically meant to avoid this — but then we copy it to storage anyway, throwing the protection away.
- **Fix:** Keep the token in a module-scoped variable only (the `let token` already exists). Drop `sessionStorage.setItem`. On hard reload, re-show the "use #token=... in URL" screen (the current flow already supports that). If cross-tab is desired, use `BroadcastChannel` with origin check, not storage. If cross-reload-without-token-in-URL is needed, accept that friction explicitly.

### Token passed as `?api_key=` query string on WebSockets (severity: medium)
- **Location:** `src/web/src/apps/chat/hooks/useTextStream.ts:43`, `useVoiceSession.ts:178`, `apps/voice/VoiceApp.tsx:80`, `apps/text/TextApp.tsx:62`, `apps/terminal/TerminalApp.tsx:75`
- **Issue:** Every WebSocket URL carries `?api_key=<fullKey>`. These URLs land in:
  - Kestrel / IIS / nginx access logs
  - Azure App Service `AppServiceHTTPLogs` (mentioned in CLAUDE.md that the app runs on Azure)
  - Docker/container stdout if anyone logs the incoming request URL
  - Browser's own DevTools / HAR exports
  - Any proxy/CDN between the client and server
- **Risk:** Real leak surface — server logs routinely aggregate to log-analytics pipelines, and the admin key never rotates. WebSocket-over-HTTPS protects the query on the wire, but every intermediary it touches has it at rest.
- **Fix:** Browsers don't let you set headers on `new WebSocket(url)` — but the standard protocol subprotocol trick works: `new WebSocket(url, ['api-key.' + token])` on the client, and `HandleAuthenticateAsync` extracts the subprotocol on the server. Or use a short-lived one-time token: client calls `POST /api/session-token`, server returns a random 5-minute key, client puts it in the URL. Server logs then expire with the token.

### `useTextStream` uses stale `streaming` in closure (severity: medium)
- **Location:** `src/web/src/apps/chat/hooks/useTextStream.ts:64`
- **Issue:** The server-pushed `delta` path checks `if (!streamContentRef.current && !streaming)` but `streaming` is the React state captured at WebSocket setup time. It never gets updated in this closure because the `useEffect` depends only on `conversationId`. If the user sends a message (setting streaming=true), and then the server subsequently pushes an unsolicited delta, the condition evaluates `!streaming` against the stale `false`, always opening a new assistant bubble. Could double-open bubbles during the streaming window.
- **Risk:** Visual glitch — duplicate "empty assistant message" UI artifacts when both user-initiated and scheduled-task messages race. Low-impact but real.
- **Fix:** Read streaming state via a ref (`streamingRef.current`) or just check `streamContentRef.current` only (if there's accumulated content, we're already streaming).

### VoiceApp cleanup doesn't close WebSocket on re-render (severity: medium)
- **Location:** `src/web/src/apps/voice/VoiceApp.tsx:262-268`
- **Issue:** The cleanup effect has an empty dependency array `[]`, and the refs it accesses (`wsRef`, `streamRef`, `audioCtxRef`) only get their current values at unmount. But `handleStart` creates a new WebSocket/AudioContext on every click and reassigns the refs. If the user clicks Start, then Start again without hitting Stop (buggy double-click, window focus loss + reclick), the previous WebSocket is not closed — only the final one gets cleaned up. Memory + socket leak.
- **Risk:** Orphaned WebSocket + AudioContext + microphone stream → persistent microphone access indicator after the component is done. User-visible creepiness, plus connection count growth on the server.
- **Fix:** `handleStart` should call `handleStop` first (or return early if `wsRef.current` is already open). Also consider making `handleStop` idempotent.

### ExplorerApp "go up" off-by-one for nested roots (severity: low)
- **Location:** `src/web/src/apps/explorer/ExplorerApp.tsx:189-191`
- **Issue:** Going up from `a/b/c` uses `segments.slice(0, -1).join('/')` = `a/b`. Fine. But from `a` it produces `''` (root) via the same code, which is also correct, but the button is only disabled when `currentPath === ''`. If the backend returns a file with `path: '/foo'` (leading slash), splitting on `/` produces `['', 'foo']`, and navigation breaks subtly. The code currently trusts the backend to return slash-less relative paths — fine today, but not defended.
- **Risk:** Defensive — breaks if backend changes.
- **Fix:** Either normalize in a utility (`segments.filter(Boolean)`) or assert/log invariant once.

### QrCodeDisplay uses `stableOnConnected = useCallback(onConnected, [onConnected])` — no-op (severity: low)
- **Location:** `src/web/src/apps/settings/QrCodeDisplay.tsx:22`
- **Issue:** `useCallback(onConnected, [onConnected])` is identical to `onConnected` itself — the "memoization" creates no stability because the dep already changes 1:1 with the value. The 15-second polling effect depends on it, so every time the parent passes a fresh `onConnected` the interval resets.
- **Risk:** Minor — the interval resets each render but isn't a leak because the cleanup fires. Performance only.
- **Fix:** Drop the useCallback, depend on `[connectionId]` only, and read `onConnected` from a ref if stability is wanted. Or change the prop to be stable at the caller.

### TextApp WebSocket opens before first interaction (severity: low)
- **Location:** `src/web/src/apps/text/TextApp.tsx:59-105`
- **Issue:** The WebSocket is opened in a `useEffect` that fires on mount. If the user opens the Text app and never sends, a long-lived connection sits there. `ws.onclose` sets streaming to false, but there's no visibility to the user. Multiple open Text windows = multiple sockets.
- **Risk:** Minor resource use, but real — TextApp.tsx predates the chat hook refactor and does its own WS lifecycle. ChatApp (newer) is fine.
- **Fix:** Open lazily on first send, or consolidate on `useTextStream`. Or delete this screen if ChatApp supersedes it — `TextApp` and `VoiceApp` look like older standalone apps next to the newer `ChatApp`.

### Dev Vite proxy exposes token to the Vite dev server logs (severity: low)
- **Location:** `src/web/vite.config.ts:8-17`
- **Issue:** Vite in dev proxies `/api` and `/ws` to `localhost:5264`. The `?api_key=` query string lands in Vite's access output when VITE_LOGGING is debug, and in any middleware that logs request URLs.
- **Risk:** Low (dev-only, localhost); worth noting for consistency with the subprotocol fix above.
- **Fix:** Same as bug #2 — use subprotocol.

### `crypto.randomUUID` fails on non-HTTPS contexts (severity: low)
- **Location:** Multiple — `useWindowManager.ts:40,59`, `useTextStream.ts:74,115,129`, `useVoiceSession.ts:94`, `TerminalApp.tsx:14`, `VoiceApp.tsx:28,235`, `TextApp.tsx:38`, `ScheduledTasksApp.tsx:158`
- **Issue:** `crypto.randomUUID()` is only available in "secure contexts" (HTTPS, localhost, 127.0.0.1). If anyone ever deploys the UI behind plain HTTP on a non-localhost host (reverse proxy misconfig, LAN testing), every WebSocket and window open throws.
- **Risk:** Edge-case deployment breakage.
- **Fix:** A tiny polyfill utility `function uuid(): string { return crypto.randomUUID?.() ?? fallback(); }` used everywhere.

### ExplorerApp breadcrumb navigation ignores empty last segment (severity: low)
- **Location:** `src/web/src/apps/explorer/ExplorerApp.tsx:49`
- **Issue:** `currentPath ? currentPath.split('/') : []` — but if currentPath happens to end with a trailing `/` (which can happen if the backend ever normalizes differently), you get an empty final segment and the breadcrumb shows a phantom button.
- **Risk:** Defensive.
- **Fix:** `currentPath.split('/').filter(Boolean)`.

### Voice transcript_done uses data.text as final value (severity: low)
- **Location:** `src/web/src/apps/voice/VoiceApp.tsx:150,153`
- **Issue:** On `transcript_done`, the code sets the transcript to `data.text`. But the server may or may not include a final `text` field — if it doesn't, the transcript is cleared to `undefined`. Check the server contract; code assumes `text` is always present on done events.
- **Risk:** Depends on server; could erase the transcript briefly on done.
- **Fix:** Fallback `setUserTranscript(data.text ?? prev)` pattern.

### File viewer uses `filePath` endsWith for format detection (severity: low)
- **Location:** `src/web/src/apps/explorer/FileViewerApp.tsx:43-45`
- **Issue:** `filePath.endsWith('.md')` — files like `README.md.bak` won't match (acceptable), but `MyFile.MD` (uppercase) also won't. Small but documented extension detection should normalize case.
- **Risk:** Trivial UX glitch.
- **Fix:** Compare lowercased path or extension.

---

## Frontend: Smells

### No frontend tests at all (severity: medium)
- **Location:** `src/web/package.json:6-11`
- The `test` script is absent entirely. No Vitest, no React Testing Library. `useWindowManager.ts` is a pure reducer — 100% unit-testable in 30 lines. `token.ts` is 32 lines and has subtle edge cases (query vs hash precedence, sessionStorage restore). These are the lowest-cost highest-value candidates for a tiny test suite.
- Adding ~5 tests for the reducer and token extraction would catch regressions in the two most security-sensitive paths.

### Two parallel chat implementations (severity: low)
- **Location:** `src/web/src/apps/text/TextApp.tsx` vs `src/web/src/apps/chat/ChatApp.tsx`
- Both apps do WebSocket-backed text streaming. ChatApp uses hooks (`useTextStream`, `useConversation`), TextApp inlines everything into a single 196-line component. Similar for `VoiceApp.tsx` (standalone, 312 lines) vs `useVoiceSession` (hook used inside ChatApp). The standalone apps are missing the hook-based conversation-routing and drift risk — any fix to the WS protocol has to be made in two places.
- **Fix:** Delete `apps/text` and `apps/voice` if ChatApp supersedes them, or rewrite them to wrap the hooks.

### Worklet code is duplicated between VoiceApp and useVoiceSession (severity: low)
- **Location:** `useVoiceSession.ts:128-145` and `VoiceApp.tsx:185-201`
- The PCM capture worklet is identical in both files — inlined as a template-string blob. Refactor to a shared util.

### Prop drilling through ChatApp hook composition (severity: low)
- **Location:** `ChatApp.tsx`, `ConversationView.tsx`, `MessageList.tsx`
- Six callbacks (`onUserMessage`, `onAssistantStart`, `onAssistantDelta`, `onDone`, `onAppendMessage`, `onUpdateLastMessageContent`) flow between useConversation + useTextStream + useVoiceSession. Works, but the hooks know about each other's vocabulary. A small event emitter or the combined message hook would shrink the glue code.

### Inline styles mixed with CSS Modules (severity: low)
- **Location:** `App.tsx:17`, `ExplorerApp.tsx:243`, `AgentConfigForm.tsx:109,146,159,174`
- Inline `style={...}` is sprinkled in a few places that already have `.module.css`. Minor inconsistency — not broken, just a slight loss of theming discipline.

### No shared ErrorBoundary (severity: low)
- **Location:** root (App.tsx)
- If any app throws (parse error in MarkdownViewer, AudioContext failure), the whole Desktop white-screens. A root `ErrorBoundary` wrapping `WindowFrame` children would isolate failures to a single window.

### Console logging left in production bundles (severity: low)
- **Location:** `TerminalApp.tsx:76,101,107,152`, `VoiceApp.tsx:81,90,104,115,120,163,168,231`
- Not a bug, but debug-level noise shipping to prod. Vite doesn't strip `console.log` automatically. Consider a `logger.debug` wrapper that tree-shakes in production mode.

### React 19 canary APIs — unused but dependency surface (severity: low)
- **Location:** `package.json:19`
- `react@^19.2.4` is post-GA, so no canary risk. Clean.

### `any`-style escapes via `as unknown as React.ComponentType` (severity: low)
- **Location:** `ExplorerApp.tsx:63`
- `FileViewerApp as unknown as React.ComponentType<Record<string, unknown>>` — a TypeScript escape hatch. The `DynamicWindowOptions.component` type is too loose, forcing the cast. Tighten `DynamicWindowOptions.component` to `ComponentType<any>` (or keep as-is but provide a typed helper).

---

## Tests: Bugs

### Shared `DATA_DIR = /home/data` across tests, single-threaded assumption (severity: high)
- **Location:** `src/agent/OpenAgent.Tests/TestSetup.cs:17-25`
- **Issue:** `TestSetup.EnsureConfigSeeded()` writes `/home/data/config/agent.json` on first call, and sets `DATA_DIR` as a process-wide env var. Every integration test depending on `WebApplicationFactory<Program>` shares that directory. If two test classes touch agent config concurrently (xUnit parallelizes across collections by default), they race.
- **Risk:** Intermittent failures — one test sets `ActiveSkills`, another reads state and gets inconsistent data. Also: `/home/data` is a Linux path; on Windows, `Path.Combine("/home/data", "config")` becomes `C:\home\data\config` which is absolute but unusual. Tests have no `Directory.Delete` on shutdown, so the state persists across test runs.
- **Fix:** Give each test class a unique temp `DATA_DIR` via `WithWebHostBuilder` + `IConfiguration` override. The `_initialized` flag pattern fights parallelism — use `IAsyncLifetime` per class. Several tests (`SqliteConversationStoreTests`, `MemoryChunkStoreTests`, `MemoryIndexServiceTests`, `MemoryToolHandlerTests`, `DataDirectoryBootstrapTests`) already do this correctly — just propagate to the WebApplicationFactory-based tests.

### Hard-coded `dev-api-key-change-me` (severity: medium)
- **Location:** `ApiKeyAuthTests.cs:12`, plus 12 other occurrences across test files
- **Issue:** The tests depend on the exact string from `appsettings.Development.json:9` at repo root. A developer who rotates the dev key in that file breaks all tests. Also: if someone accidentally sets `Authentication:ApiKey` in a local env var when running tests, the resolver takes precedence and the tests 401.
- **Risk:** Flaky dev environment; hidden coupling.
- **Fix:** Override `Authentication:ApiKey` per test via `WithWebHostBuilder.ConfigureAppConfiguration` to a known test value like `test-api-key`. Same pattern as the rest of the DI overrides.

### `MemoryChunkStoreTests` / `MemoryIndexServiceTests` share `Path.GetTempPath()` unique-per-test but use `SqliteConnection.ClearAllPools()` (severity: medium)
- **Location:** `MemoryChunkStoreTests.cs:24`, `MemoryIndexServiceTests.cs:29`, `MemoryToolHandlerTests.cs:30`, `MemoryIndexHostedServiceTests.cs:29`
- **Issue:** `ClearAllPools()` is process-wide — it clears ALL pool connections, not just the ones this test opened. If another test is concurrently using SQLite, it will reopen. This pattern works because tests are serial within a collection, but if xUnit parallelism is ever enabled across these classes, they'll interact.
- **Risk:** Latent flakiness under parallelism.
- **Fix:** Use unique connection strings per test (already done via unique `_dbPath`) and `_store.Dispose()` properly; avoid `ClearAllPools`. Or mark with `[Collection]`.

### `SqliteConversationStoreTests.Compaction_summarizes_old_messages_and_updates_cutoff` uses `Task.Delay(500)` (severity: medium)
- **Location:** `SqliteConversationStoreTests.cs:142`
- **Issue:** Background compaction is fire-and-forget. Tests sleep 500ms and assume it finished. On a slow CI agent this will flake.
- **Risk:** Flaky under load.
- **Fix:** Add a `TaskCompletionSource` to the fake summarizer that the test can `await` explicitly, or expose a "wait for compaction idle" signal on the store.

### `FakeVoiceProvider.WaitForSessionAsync` polls 50x20ms (severity: low)
- **Location:** `VoiceWebSocketTests.cs:128-139`
- **Issue:** 1-second wall-clock max for the voice session to materialize. Works locally, may timeout on cold CI starts.
- **Risk:** Flaky under load.
- **Fix:** Expose a `TaskCompletionSource<FakeVoiceSession>` from `StartSessionAsync` and await directly.

### `VoiceWebSocketTests.VoiceEndpoint_NonWebSocket_Returns400` may not test the right thing (severity: low)
- **Location:** `VoiceWebSocketTests.cs:39-47`
- **Issue:** The test asserts `400` from a plain GET to `/ws/conversations/test-123/voice`. That's the expected behavior when the middleware sees a non-WS upgrade, but it could also come from 404 or auth failure. The test header includes `X-Api-Key` so auth is fine, but the assertion is coarse. If someone refactors and returns 426 Upgrade Required instead, this fails for the wrong reason.
- **Fix:** Assert on the response body / log as well.

### `ApiKeyResolverTests.Resolve_WithExistingFileMissingApiKey_PreservesOtherFieldsAndAddsGeneratedKey` (severity: low)
- **Location:** `ApiKeyResolverTests.cs:76-88`
- **Issue:** The input JSON `"""{"symlinks":{"media":"D:\\Media"}}"""` relies on JSON-escaped backslashes. Fine, but this is the only test that verifies non-top-level preservation. If Resolve ever loses nested-object fidelity, only this one test catches it.

### `Fakes/InMemoryConversationStore.GetMessagesByIds` returns in arbitrary order (severity: low)
- **Location:** `Fakes/InMemoryConversationStore.cs:176-183`
- **Issue:** `.SelectMany(...).Where(...).ToList()` doesn't preserve the `messageIds` input order. The real SQLite store does preserve order (ORDER BY rowid). Tests that assume preserved order may pass or fail depending on which store they use.
- **Risk:** Contract drift — fake doesn't match real behavior.
- **Fix:** `messageIds.Select(id => _messages.Values.SelectMany(ms => ms).FirstOrDefault(m => m.Id == id)).Where(m => m is not null)` to preserve the request order.

### Comment/behavior drift in `Fakes/InMemoryConversationStore.UpdateChannelMessageId` (severity: low)
- **Location:** `Fakes/InMemoryConversationStore.cs:124-148`
- **Issue:** Says "Message is immutable (init properties), so replace with a copy" — but doesn't copy `PromptTokens`, `CompletionTokens`, `ElapsedMs` like `AddMessage` does. The resulting message drops token counts silently. Tests won't catch it because they don't assert on token fields after channel id update.
- **Fix:** Copy all fields (or define a `with` record literal, or switch the model to a record).

### `ThrowingTextProvider` yields after throw (pragma warning hack) (severity: low)
- **Location:** `Fakes/ThrowingTextProvider.cs:26-28,38-40`
- **Issue:** `yield break` after `throw` is unreachable code, disabled with `#pragma warning disable CS0162`. The throw is in the async-iterator setup, not yielded — so consumers see the exception on first `MoveNextAsync()`. Works, but a cleaner pattern is `IAsyncEnumerable<CompletionEvent> CompleteAsync(...) => ThrowingImpl()` delegating to a private non-iterator method.

### Dispose swallows exceptions (severity: low)
- **Location:** `SqliteConversationStoreTests.cs:25`, `MemoryChunkStoreTests.cs:25`, many others
- **Issue:** `try { Directory.Delete(...) } catch { }` silently swallows cleanup failures. If a test leaks a file handle, the next run of the same test on the same machine fails with a confusing "unique temp dir not so unique" because a stale one is around.
- **Fix:** At minimum log the exception to test output. Use `TestContext` or `ITestOutputHelper`.

---

## Tests: Smells

### No test for endpoints in `/api/admin/*`, `/api/tools`, `/api/files*`, `/api/logs*`, `/api/memory-index`, `/api/connections` (severity: high)
- Seven endpoint files in `src/agent/OpenAgent.Api/Endpoints/` have zero tests. `AdminEndpoints.cs` (provider config, system prompt reload), `ToolEndpoints.cs`, `FileExplorerEndpoints.cs` (security-critical path traversal), `LogEndpoints.cs` (query parsing), `ConnectionEndpoints.cs` — all untested. See Coverage section below.

### Test method names inconsistent (severity: low)
- Mix of `Method_Scenario_Expectation` (`Request_WithoutApiKey_Returns401`), `HandleUpdateAsync_Valid...` (PascalCase_verb), and `snake_case` (`GetMessages_excludes_compacted_messages`). Agree on one style.

### Integration tests create clients per-test but the factory is class-level (severity: low)
- Every `[Fact]` creates a fresh `HttpClient` via `_factory.CreateClient()`. The `WithWebHostBuilder` block runs once per class which is fine, but there's no per-test reset of the shared `IConversationStore`. So tests leak state from one to another even within a class. `ListConversations_OrdersByLastActivity` works around this by filtering to its known IDs, acknowledging the state leak.
- **Fix:** Use `IAsyncLifetime` to reset the store per-test, or isolate via a transaction-rolling-back fixture.

### Repeated auth boilerplate (severity: low)
- Every integration test has:
  ```
  var client = _factory.CreateClient();
  client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");
  ```
  Extract to a `CreateAuthenticatedClient()` helper on a base class (ScheduledTaskEndpointTests already does this at line 35-40; replicate).

### Test naming typos / vagueness (severity: low)
- `SqliteConversationStoreTests.GetMessages_populates_RowId` — clear.
- `VoiceEndpoint_BinaryAudio_IsForwardedToVoiceSession` — good.
- `Run_WithExistingSymlinkToDifferentTarget_LeavesItUntouched` — good.
- `HealthEndpointTests` has one trivial test — fine but could combine with ApiKeyAuthTests's `HealthEndpoint_WithoutApiKey_Returns200` duplicate.

### Fake provider Key duplication (severity: low)
- Every fake text provider is named `Key => "fake-*"` or similar. Consider a constant on a shared test-base, or use `nameof(typeof)` to derive. Current approach is fine but there are ~4 of these scattered.

### Missing `[Trait("Category", ...)]` or `[Collection(...)]` (severity: low)
- Slow tests (embedding-provider E2E, compaction, hosted service) aren't separated from unit tests. CI has no way to say "just run the fast ones on every commit, full suite nightly." Only `OnnxBgeEmbeddingProviderTests.GenerateEmbeddingAsync_returns_unit_vector_and_distinguishes_query_and_passage` uses a skip pattern (returns early if model not present), but the rest of the slow tests just run.

---

## Coverage gaps (ranked)

1. **Frontend has no tests (0 files, 0 assertions).** Critical. At minimum:
   - `auth/token.ts` — query vs hash precedence, sessionStorage restore, hash-stripping.
   - `hooks/useWindowManager.ts` — pure reducer with 9 actions; adding 10 tests for OPEN/CLOSE/FOCUS/MAXIMIZE/RESTORE/VIEWPORT_RESIZE is a one-hour job and protects the entire desktop.
   - `MarkdownViewer` frontmatter parsing — the hand-rolled YAML parser at `MarkdownViewer.tsx:16-36` has edge cases (colons in values, whitespace, missing closing `---`).
   - `JsonlViewer` — log-entry parsing and level mapping.

2. **AgentLogic + SystemPromptBuilder have no direct unit tests.** The integration test `SkillIntegrationTests` touches `SystemPromptBuilder.Build`, but there's no test for:
   - Day-of-week / ISO week number injection (hardcoded Europe/Copenhagen)
   - Filtering by `ConversationType` (Voice vs Text vs ScheduledTask)
   - Compaction context formatting
   - BOOTSTRAP.md self-delete ritual
   These are easy to regress with a LLM-provider change and the team will never notice.

3. **Admin / provider endpoints (`AdminEndpoints.cs`) have no tests.** The `saveProviderConfig` flow (`GET /api/admin/providers/{key}/values` → edit → `POST .../config`) is heavily used by the UI and handles secrets (`***` masking roundtrip). The Secret-field roundtrip logic in `ProviderForm.tsx:30-48` has real bug potential (what if the user intentionally blanks a secret?) and there's no test for either side.

4. **File explorer endpoints (`FileExplorerEndpoints.cs`) have no tests.** This is the path-traversal attack surface (user-controlled `?path=` on every file call). `renameFile`, `deleteFile`, `uploadFiles`, `createDirectory` all take untrusted paths and the only test coverage lives in `FileSystemErrorMessageTests` (unit-level string formatting). A single integration test that asserts `?path=../etc/passwd` returns 4xx would be high-value.

5. **ConnectionManager lifecycle.** Tests for `TelegramMessageHandler` are thorough but the `ConnectionManager` (IHostedService that starts/stops channel providers) has no test. Dictionary races, start-twice idempotency, dispose-on-shutdown, and factory-matching by type are all untested.

6. **Memory search ranking / `search_memory` tool.** `MemoryToolHandlerTests` verifies the tool exists unconditionally (good); no test for `SearchMemoryTool.ExecuteAsync` with real hybrid vector+FTS5 scoring.

7. **WebSocket text endpoint.** `WebSocketTextEndpoints.cs` has no integration test (only the voice variant does). The text-stream protocol (delta / tool_call / tool_result / done) is contractual and drift-prone.

---

## Open Questions

1. Is the URL-hash token handoff documented anywhere visible to end-users? If the intent is "one-shot auth then session-bound," then `sessionStorage` undermines the design — worth a discussion on which way to go.
2. Would a subprotocol-based WS auth be acceptable as the standard pattern, so we stop bleeding `api_key` into server logs? Easy change.
3. Any appetite for a tiny Vitest setup? Three or four tests against the pure reducer would be the fastest ROI in this codebase.
4. The `dev-api-key-change-me` literal in `appsettings.Development.json` looks like it was hand-coded for testing. Should this be moved entirely into the test project (via `WithWebHostBuilder`), and have prod/dev always resolve from `config/agent.json`? The current split is confusing.
5. `MemoryChunkStoreTests` and friends call `SqliteConnection.ClearAllPools()` in Dispose — global state. Is this needed only on Windows? Could we switch to `:memory:` stores in tests to avoid the temp-dir dance entirely?

---

## Files reviewed

Frontend (all of `src/web/src/`):
- `src/web/src/App.tsx`
- `src/web/src/main.tsx`
- `src/web/src/auth/token.ts`
- `src/web/src/auth/api.ts`
- `src/web/src/desktop/Desktop.tsx`
- `src/web/src/desktop/TopBar.tsx`
- `src/web/src/desktop/StartMenu.tsx`
- `src/web/src/hooks/useWindowManager.ts`
- `src/web/src/windows/WindowFrame.tsx`
- `src/web/src/windows/WindowContext.ts`
- `src/web/src/windows/types.ts`
- `src/web/src/apps/registry.ts`
- `src/web/src/apps/types.ts`
- `src/web/src/apps/chat/ChatApp.tsx`
- `src/web/src/apps/chat/hooks/useConversation.ts`
- `src/web/src/apps/chat/hooks/useConversations.ts`
- `src/web/src/apps/chat/hooks/useTextStream.ts`
- `src/web/src/apps/chat/hooks/useVoiceSession.ts`
- `src/web/src/apps/chat/components/ConversationSidebar.tsx`
- `src/web/src/apps/chat/components/ConversationView.tsx`
- `src/web/src/apps/chat/components/MessageList.tsx`
- `src/web/src/apps/chat/components/Composer.tsx`
- `src/web/src/apps/settings/SettingsApp.tsx`
- `src/web/src/apps/settings/ProviderForm.tsx`
- `src/web/src/apps/settings/ConnectionsForm.tsx`
- `src/web/src/apps/settings/SystemPromptForm.tsx`
- `src/web/src/apps/settings/AgentConfigForm.tsx`
- `src/web/src/apps/settings/QrCodeDisplay.tsx`
- `src/web/src/apps/settings/api.ts`
- `src/web/src/apps/explorer/ExplorerApp.tsx`
- `src/web/src/apps/explorer/FileViewerApp.tsx`
- `src/web/src/apps/explorer/ContextMenu.tsx`
- `src/web/src/apps/explorer/api.ts`
- `src/web/src/apps/explorer/viewers/TextViewer.tsx`
- `src/web/src/apps/explorer/viewers/MarkdownViewer.tsx`
- `src/web/src/apps/explorer/viewers/JsonlViewer.tsx`
- `src/web/src/apps/conversations/ConversationsApp.tsx`
- `src/web/src/apps/conversations/api.ts`
- `src/web/src/apps/scheduled-tasks/ScheduledTasksApp.tsx`
- `src/web/src/apps/scheduled-tasks/api.ts`
- `src/web/src/apps/voice/VoiceApp.tsx`
- `src/web/src/apps/text/TextApp.tsx`
- `src/web/src/apps/terminal/TerminalApp.tsx`
- `src/web/vite.config.ts`
- `src/web/package.json`
- `src/web/tsconfig.json`, `tsconfig.app.json`, `tsconfig.node.json`
- `src/web/eslint.config.js`
- `src/web/index.html`

Tests (full directory):
- `src/agent/OpenAgent.Tests/TestSetup.cs`
- `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj`
- `src/agent/OpenAgent.Tests/HealthEndpointTests.cs`
- `src/agent/OpenAgent.Tests/ApiKeyAuthTests.cs`
- `src/agent/OpenAgent.Tests/ApiKeyResolverTests.cs`
- `src/agent/OpenAgent.Tests/RootResolverTests.cs`
- `src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs`
- `src/agent/OpenAgent.Tests/ChatEndpointTests.cs`
- `src/agent/OpenAgent.Tests/ConversationEndpointTests.cs`
- `src/agent/OpenAgent.Tests/ScheduledTaskEndpointTests.cs`
- `src/agent/OpenAgent.Tests/VoiceWebSocketTests.cs`
- `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`
- `src/agent/OpenAgent.Tests/TelegramWebhookEndpointTests.cs`
- `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`
- `src/agent/OpenAgent.Tests/MemoryChunkStoreTests.cs`
- `src/agent/OpenAgent.Tests/MemoryIndexServiceTests.cs`
- `src/agent/OpenAgent.Tests/MemoryIndexHostedServiceTests.cs`
- `src/agent/OpenAgent.Tests/MemoryToolHandlerTests.cs`
- `src/agent/OpenAgent.Tests/OnnxBgeEmbeddingProviderTests.cs`
- `src/agent/OpenAgent.Tests/ExpandToolTests.cs`
- `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`
- `src/agent/OpenAgent.Tests/Fakes/FakeTelegramSender.cs`
- `src/agent/OpenAgent.Tests/Fakes/FakeTelegramTextProvider.cs`
- `src/agent/OpenAgent.Tests/Fakes/FakeWhatsAppSender.cs`
- `src/agent/OpenAgent.Tests/Fakes/FakeConnectionStore.cs`
- `src/agent/OpenAgent.Tests/Fakes/FakeEmbeddingProvider.cs`
- `src/agent/OpenAgent.Tests/Fakes/StreamingTextProvider.cs`
- `src/agent/OpenAgent.Tests/Fakes/ThrowingTextProvider.cs`
- `src/agent/OpenAgent.Tests/Skills/SkillIntegrationTests.cs`
- Referenced but not deeply read: `OnnxMultilingualE5EmbeddingProviderTests.cs`, `MemoryChunkerTests.cs`, `Skills/SkillCatalogTests.cs`, `Skills/SkillDiscoveryTests.cs`, `Skills/SkillFrontmatterParserTests.cs`, `Skills/SkillToolHandlerTests.cs`, `Installer/*`, `WebFetch/*`, `ConversationTools/*`, `FileSystemErrorMessageTests.cs`, `SystemPromptSymlinkBlockTests.cs`, `TelegramMarkdownConverterTests.cs`, `WhatsAppMessageHandlerTests.cs`, `WhatsAppMarkdownConverterTests.cs`, `WhatsAppNodeProcessTests.cs` — names and counts only
