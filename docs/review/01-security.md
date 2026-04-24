# Security & Auth Review — 2026-04-23

## TL;DR
Overall posture is solid for a self-hosted, single-tenant agent: every non-health endpoint is gated by `RequireAuthorization()` behind `X-Api-Key`, secrets stay off the wire, and SSRF blocking is thoughtful for both literal IPs and DNS results. The important gaps are (1) a path-traversal class bug that repeats in eight places because every scope check uses a bare `fullPath.StartsWith(root)` with no separator guard, (2) the WebFetch HttpClient auto-follows redirects without re-validating the target (bypasses the SSRF fix CLAUDE.md notes as closed), (3) `GET /api/connections` returns connection `Config` (Telegram bot tokens, WhatsApp creds) **unmasked** while the parallel `/api/admin/providers` path masks secrets correctly, and (4) the API-key comparison is not constant-time. A cluster of DoS/unbounded-read smells round out the list.

## Strengths
- Every non-health endpoint is wrapped in `RequireAuthorization()` — confirmed across `ChatEndpoints.cs:67`, `ConversationEndpoints.cs:20`, `FileExplorerEndpoints.cs:19`, `LogEndpoints.cs:29`, `AdminEndpoints.cs:18`, `ConnectionEndpoints.cs:34,36`, `ScheduledTaskEndpoints.cs:20`, `ToolEndpoints.cs:21`, `WebSocketVoiceEndpoints.cs:84`, `WebSocketTextEndpoints.cs:67`, `WebSocketTerminalEndpoints.cs:110`, `MemoryIndex/MemoryIndexEndpoints.cs:14`, `WhatsAppEndpoints.cs:42`, `SystemPromptEndpoints.cs:27`. The `/health` anonymous exception is intentional and minimal (`Program.cs:253-261`).
- SSRF protection is thorough for both literal IPs and DNS-resolved IPs: blocks RFC1918, loopback (127/8, 0/8), link-local 169.254/16 incl. AWS metadata, CGN (100.64/10), benchmark (198.18/15), and IPv6 `::1`, fe80::/10 link-local, fc00::/7 ULA; `IsIPv4MappedToIPv6` normalisation included, unknown address families fail-closed (`UrlValidator.cs:79-128`). WebFetch delegates to `ValidateWithDnsAsync` before sending any request (`WebFetchTool.cs:48`).
- Telegram webhook secret-token comparison uses `CryptographicOperations.FixedTimeEquals` (`TelegramWebhookEndpoints.cs:47-53`) — the right pattern; belongs in the main auth handler too.
- API key generation uses `RandomNumberGenerator.GetBytes(24)` → 48 hex characters, 192-bit entropy (`ApiKeyResolver.cs:46`).
- Admin endpoint masks secret-typed provider fields with `"***"` when returning saved config (`AdminEndpoints.cs:64-78`). Secret fields are declared via `Type = "Secret"` across every provider + the Telegram `botToken`.
- SQL queries in `SqliteConversationStore` use parameterized commands throughout; the one dynamic `IN (...)` construction builds parameter names from a generated sequence, not user input (`SqliteConversationStore.cs:379-383`).
- Shell tool resolves the `cwd` input against the workspace root and rejects escapes before spawning (`ShellExecTool.cs:68-78`); process-tree kill on timeout is implemented via `Process.Kill(entireProcessTree: true)` (`ShellExecTool.cs:216`).
- Skills discovery enforces a 256 KB cap per `SKILL.md` and skips hidden/noise directories (`SkillDiscovery.cs:17-47`).
- `/data/` and `config/` are gitignored so provider-config files containing real API keys aren't committed (`.gitignore:13-14`).
- Webhook accepted as anonymous is justified by a comment and uses webhookId-in-URL + shared-secret header (`TelegramWebhookEndpoints.cs:84`).

## Bugs

### Path traversal via prefix-only root check — repeated across 8 call sites (severity: high)
- **Location:** `FileReadTool.cs:39`, `FileWriteTool.cs:40`, `FileAppendTool.cs:39`, `FileEditTool.cs:43`, `FileExplorerEndpoints.cs:264` (`ResolveSafePath`), `LogEndpoints.cs:74`, `ShellExecTool.cs:73`, `SkillToolHandler.cs:249` (`ActivateSkillResourceTool`).
- **Issue:** Every "is this path inside my root?" check is `fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)` with no separator guard. When `root = "C:\\data"`, the user-supplied path `..\\data-evil\\secrets.txt` resolves to `C:\\data-evil\\secrets.txt`, and `"C:\\data-evil\\secrets.txt".StartsWith("C:\\data")` is **true**. Same on Linux: `/home/agent/data` vs `/home/agent/data_backup/credentials`.
- **Risk / exploit:** An agent (or anything that can influence tool arguments — a memory file, a skill, an attacker-controlled URL fetched by WebFetch and pasted into a prompt) can read / write / edit / execute-from any sibling directory of the data root that shares the root name as a prefix. In the Windows service deployment (`DATA_DIR` falls back to `AppContext.BaseDirectory`), a sibling `C:\OpenAgent-backup\` or `C:\OpenAgentLogs\` is reachable. In the Azure container (`/home/data`), `/home/data-restore/` is reachable. The `LogEndpoints` bug lets the web API read any file whose path starts with the `logs/` directory prefix — a `?filename=../logsBACKUP/...` works.
- **Fix:** Replace with `fullPath.Equals(root, cmp) || fullPath.StartsWith(root + Path.DirectorySeparatorChar, cmp)`, or use `Path.GetRelativePath(base, resolved)` and reject results starting with `..`. Extract a `PathScope.IsInside(root, candidate)` helper into `OpenAgent.Contracts` so the rule lives once; the current bug repeats because each tool re-implements the check.

### Connection configs (bot tokens, etc.) returned unmasked (severity: high)
- **Location:** `ConnectionEndpoints.cs:160-170` (`ToResponse`), triggered from the LIST, GET-by-id, CREATE and UPDATE responses (lines 39-52, 86, 124).
- **Issue:** `GET /api/connections` and `GET /api/connections/{id}` return `connection.Config` as a raw `JsonElement`. For Telegram, this includes `botToken` (declared `Type = "Secret"` in `TelegramChannelProviderFactory.cs:27`). The provider/admin endpoint masks secrets (`AdminEndpoints.cs:64-78`), but the parallel connection endpoint does not — `ToResponse` copies `Config` through verbatim.
- **Risk / exploit:** Anyone with a valid `X-Api-Key` can dump every connected channel's bot token / credentials. Since the API key is a single bearer credential with no rotation, one leaked key = full credential theft from every channel. Also violates the principle already established for provider config.
- **Fix:** Apply the same secret-masking loop from `AdminEndpoints` inside `ToResponse`: look up the matching `IChannelProviderFactory.ConfigFields` by `connection.Type`, replace any field where `Type == "Secret"` with `"***"` before returning. Same for the echo response on CREATE / UPDATE / start / stop.

### SSRF via HTTP redirect — WebFetch auto-follows to private IPs (severity: high)
- **Location:** `WebFetchToolHandler.cs:14` (HttpClient construction) + `WebFetchTool.cs:61-63` (SendAsync).
- **Issue:** `new HttpClient { Timeout = ... }` uses the default handler with `AllowAutoRedirect = true`. `UrlValidator.ValidateWithDnsAsync` validates only the initial URL's DNS. If an attacker controls a public URL that 302-redirects to `http://169.254.169.254/...` (Azure IMDS / AWS metadata) or `http://localhost:8080/api/memory-index/run`, the redirect is followed without re-validation.
- **Risk / exploit:** CLAUDE.md notes issue #7 (WebFetch SSRF) as closed with "DNS rebinding accepted as risk". Redirects are a separate bypass channel not mentioned — the fix therefore doesn't hold end-to-end. Any call to `web_fetch("https://attacker.example")` can be turned into a fetch against internal services (Azure IMDS, the agent's own `/api/*` on localhost, RFC1918 hosts on the same VNet). Output comes back to the agent as markdown — IMDS tokens, error disclosures, and internal-API payloads are all exfiltrable through the model.
- **Fix:** Build an `HttpClientHandler { AllowAutoRedirect = false }` (or `SocketsHttpHandler`) and handle redirects explicitly, re-running `ValidateWithDnsAsync` on each `Location` header. As a cheaper partial fix, set `AllowAutoRedirect = false` and report 3xx as a tool error. As a bonus, `SocketsHttpHandler.ConnectCallback` that validates the actual connected IP also closes the DNS-rebinding TOCTOU that CLAUDE.md currently accepts.

### API key comparison is not constant-time (severity: medium)
- **Location:** `ApiKeyAuthenticationHandler.cs:40`.
- **Issue:** `string.Equals(providedKey, Options.ApiKey, StringComparison.Ordinal)` short-circuits on the first differing byte. The Telegram webhook already uses `CryptographicOperations.FixedTimeEquals` (`TelegramWebhookEndpoints.cs:47`); the main auth path doesn't.
- **Risk / exploit:** Network-local attackers can statistically differentiate response times per byte of the key and recover it. Harder over the public internet (HTTPS + jitter masks sub-microsecond differences) but realistic over a LAN or co-located VM. For a 48-char hex key this is a full recovery from minutes of timing samples.
- **Fix:** `CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedKey), Encoding.UTF8.GetBytes(Options.ApiKey))`. If lengths differ, early-return `Fail` only *after* a dummy comparison (or XOR the length difference into the result) to avoid a length oracle.

### WebFetch response body read before truncation cap (severity: medium)
- **Location:** `WebFetchTool.cs:73-76`.
- **Issue:** `var html = await response.Content.ReadAsStringAsync(ct)` materialises the entire response body into a string, then `if (html.Length > MaxResponseBytes) html = html[..MaxResponseBytes]` trims after the fact. `MaxResponseBytes = 2_000_000` (2 MB) but nothing prevents the server from streaming hundreds of MB; .NET buffers it all before the trim runs.
- **Risk / exploit:** A malicious fetch target (or a buggy one) can OOM the agent process. The 30 s HttpClient timeout limits exposure, but large fast responses can hit before timeout. In the Azure container (1.75 GB RAM) this means service crash + restart loop.
- **Fix:** Read as a length-limited stream: `HttpCompletionOption.ResponseHeadersRead` + `response.Content.ReadAsStreamAsync`, then copy into a capped `MemoryStream` / pooled buffer with `Stream.ReadAsync` in a loop, breaking at `MaxResponseBytes`. Also pre-check `Content-Length` and skip non-HTML content types.

### Webhook trigger reads request body with no size limit (severity: medium)
- **Location:** `ScheduledTaskEndpoints.cs:97-110`.
- **Issue:** `var body = await reader.ReadToEndAsync(ct)` pulls the entire body into a string. ASP.NET Core's default `MaxRequestBodySize` (~30 MB) applies, but nothing prevents a sustained flood of 30 MB payloads against `/api/scheduled-tasks/{id}/trigger`. The body is then concatenated straight into a prompt (`{task.Prompt}\n\n<webhook_context>\n{body}\n</webhook_context>`) and handed to the LLM.
- **Risk / exploit:** Authenticated DoS on a 1.75 GB App Service is trivial. Also a prompt-injection vector: attacker-controlled body text becomes part of the LLM turn without escaping — an attacker can include `</webhook_context>\n...` to try to break out of the sandbox.
- **Fix:** Enforce an explicit cap (e.g. 64 KB) by checking `request.ContentLength` up front and reading via a length-limited stream. Sanitise/escape the `</webhook_context>` terminator, or pass the body as a separate user message rather than interpolating into a prompt.

### Gemini API key embedded in WebSocket URL (secret exposure) (severity: medium)
- **Location:** `GeminiLiveVoiceSession.cs:128-131`.
- **Issue:** `?key={_config.ApiKey}` in the WS URL. If `_ws.ConnectAsync(uri, ...)` throws, the exception message includes the URI; default .NET logging of `Exception.ToString()` then leaks the key into `{dataPath}/logs/log-*.jsonl`. Other providers (Grok, Azure Realtime, Azure Chat) correctly use headers (`GrokVoiceSession.cs:51`, `AzureOpenAiVoiceSession.cs:50`, `AzureOpenAiTextProvider.cs:55`).
- **Risk / exploit:** The Gemini key lands in log files and is therefore reachable via the file-explorer API, the log-viewer UI, and Azure's log stream. Anyone with the API key can read those logs.
- **Fix:** Gemini Live accepts headers; move the key to `ClientWebSocketOptions.SetRequestHeader("x-goog-api-key", ...)`. If query-string is unavoidable, at minimum catch `ConnectAsync` failures and rethrow with a sanitised message.

### Anthropic / Azure error-body logging may echo request details (severity: medium)
- **Location:** `AnthropicSubscriptionTextProvider.cs:128-133, 367-373`; `AzureOpenAiTextProvider.cs:99-106, 299-310`.
- **Issue:** On non-2xx, the full response body is logged at Error level and also embedded in the thrown `HttpRequestException.Message`. Upstream-provider error bodies can echo submitted headers, deployment names, and occasionally request snippets — and the exception then propagates to endpoint handlers, some of which surface it to the API response.
- **Risk / exploit:** Not known to echo `Authorization` today, but the practice removes error-message defense-in-depth. Also leaks system-prompt fragments into logs when the API rejects a prompt — the log viewer then exposes them.
- **Fix:** Log status + a truncated (e.g. 500-char) body. Throw a wrapper exception with a sanitised message. Never put the raw body into an exception surfaced beyond the provider boundary.

### `ExtractEmbeddedWwwroot` recursively deletes a potentially attacker-controlled directory (severity: medium)
- **Location:** `Program.cs:50-69`.
- **Issue:** Every boot calls `Directory.Delete(wwwrootPath, recursive: true)` before re-extracting from the embedded zip. On Windows, `Directory.Delete(..., recursive: true)` **follows directory junctions**. A non-admin user with write access to the install directory (e.g. `C:\OpenAgent\`) can replace `wwwroot` with a junction pointing at `C:\Windows\System32`, and the next service restart (running as `LocalSystem`) will recursively delete the junction's target.
- **Risk / exploit:** Requires write access to the install directory, so the exposure is bounded — but the Windows service runs as `LocalSystem`, so a non-admin local user who has `Write` on `C:\OpenAgent\` (e.g. via a sloppy installer) can leverage this into privileged destructive action on the next restart.
- **Fix:** Before deleting, check `new DirectoryInfo(wwwrootPath).Attributes.HasFlag(FileAttributes.ReparsePoint)` and refuse to delete junctions. Or keep a manifest of extracted entries and remove only those. Or extract to a versioned subdir (`wwwroot/v{assemblyVersion}/`) so upgrades replace by path, not by recursive delete.

## Smells

### No CORS policy explicitly configured (severity: medium)
- **Location:** `Program.cs` — no `AddCors` / `UseCors` calls anywhere.
- **Issue:** ASP.NET's default is to reject cross-origin preflighted fetches, so today the lack of a policy is actually safe for browsers. But if anyone later moves the token to a cookie (see next smell), the absence of a deliberate posture becomes a trap.
- **Suggestion:** Add `AddCors` with an empty allowlist and a commented-out reverse-proxy override; pair with `AddRateLimiter` later.

### Token handed to the SPA via URL fragment, then parked in `sessionStorage` (severity: medium)
- **Location:** `Program.cs:234-236` (mints the URL with `#token=`), matching SPA loader in `src/web/src/auth/token.ts`.
- **Issue:** The master API key lives in `window.location.hash`, then is copied to `sessionStorage`. Any same-origin XSS (from a future bug in a React component that renders user-supplied HTML) exfiltrates the master credential, which has no scope, no expiry, and no revocation path — the only response is to hand-edit `agent.json`.
- **Suggestion:** Convert the hash to a one-shot exchange: `/auth/exchange?token=<fragment>` issues an `HttpOnly; Secure; SameSite=Strict` cookie and returns 204; the hash is cleared client-side. Subsequent API + WebSocket calls use cookie auth. For WebSockets, replace `?api_key=` with a short-lived ticket from a REST endpoint.

### API key printed to stdout at startup (severity: medium)
- **Location:** `Program.cs:234-236`.
- **Issue:** Intentional for local Ctrl-click ergonomics. In the Windows service path this is captured by the EventLog provider (`Program.cs:78`), in the Azure App Service container it goes to the log stream, and in both cases the key ends up in persistent log infrastructure. `docker logs` likewise.
- **Suggestion:** Print the full URL only when stdout is a TTY (`!Console.IsOutputRedirected`); print a fingerprint (`SHA256(apiKey)[0..8]`) otherwise so operators can still confirm which key is live.

### No rate limiting on tool execution or auth attempts (severity: medium)
- **Location:** `Program.cs` (no `AddRateLimiter`); affects `ToolEndpoints.cs:52-76`, `WebFetchTool.cs`, `ApiKeyAuthenticationHandler.cs`.
- **Issue:** `POST /api/tools/{toolName}/execute` lets any authenticated caller fire `shell_exec`, `web_fetch`, `file_write`, etc. with arbitrary arguments. Compromise of the API key goes from "read everything" to "full RCE" with no throttle signal. No backoff on repeated `X-Api-Key` failures either — brute-force would be bounded only by Kestrel.
- **Suggestion:** `AddRateLimiter` with per-IP fixed windows on: failed auth (short window, aggressive), `/api/tools/*/execute` (slow slope), `/api/webhook/*` (flood protection).

### Shell runs with service privileges, no sandbox (severity: medium)
- **Location:** `ShellExecTool.cs:86-125`; CLAUDE.md notes the service account is `LocalSystem`.
- **Issue:** `shell_exec` inherits service privileges (`LocalSystem` on Windows installs). A single prompt-injection that triggers `shell_exec` = `SYSTEM` RCE. The working-directory scoping doesn't constrain what the spawned bash reads/writes.
- **Suggestion:** Document in CLAUDE.md and install docs. Recommend a lower-privilege account (`NT SERVICE\OpenAgent`) in the installer, add a preflight that warns if the service is `LocalSystem`. Long-term: spawn tool calls as a separate UID with Job Object / cgroup resource caps.

### FileEditTool / FileAppendTool don't cap new content size (severity: low)
- **Location:** `FileEditTool.cs:71-76`, `FileAppendTool.cs:53`.
- **Issue:** `FileWriteTool.cs:51` guards `content.Length > maxFileSize`, but `file_edit`'s `new_text` is unbounded and `file_append` never checks the resulting file size. A loop of "append 1 MB of X" fills the disk.
- **Suggestion:** Apply the 1 MB `maxFileSize` cap to `newText.Length` for edit, and `existingFileSize + content.Length` for append.

### Agent-writable files become part of the system prompt (severity: low, design trade-off)
- **Location:** `FileWriteTool.cs`, `FileAppendTool.cs`, `FileEditTool.cs` + `SystemPromptBuilder.LoadFiles` (reads `AGENTS.md`, `SOUL.md`, `USER.md`, etc. on startup).
- **Issue:** The file tools can overwrite identity files; a prompt-injection in one conversation can poison the system prompt of every future conversation. Design intent — but it's a security-relevant property that should be explicit.
- **Suggestion:** Add a denylist for the identity files in `FileWriteTool`, or explicitly accept the trade-off in CLAUDE.md.

### Generated API key is persisted plaintext with default file mode (severity: low)
- **Location:** `ApiKeyResolver.cs:66-90`, `FileConfigStore.cs:17-20`.
- **Issue:** `File.WriteAllText` uses the process umask. Config files on Linux end up 0644 by default. Contains the master API key, Anthropic setup-token, WhatsApp credentials, Telegram bot tokens.
- **Suggestion:** `File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite)` on write. On Windows, an ACL restricting to `LocalSystem` + admins.

### Telegram webhook processes updates fire-and-forget without a bounded queue (severity: low)
- **Location:** `TelegramWebhookEndpoints.cs:70-81`.
- **Issue:** `_ = Task.Run(...)` per update — unbounded concurrency. Hostile (or just busy) bot traffic → thread-pool exhaustion.
- **Suggestion:** Route updates through a `Channel<Update>` with a single consumer; or cap concurrency with `SemaphoreSlim`.

### Installer `exePath` passes through string-interpolated shell arguments (severity: low)
- **Location:** `ServiceInstaller.cs:17-19`, `InstallerCli.cs:53`, `PreInstallChecks.cs:35-43`.
- **Issue:** `binPath = $"\\\"{exePath}\\\" --service"` then wrapped again with double quotes. `VerifyPathSafe` only rejects `\0\r\n`. A path with `"`, `\`, `&`, `|`, `^`, `<`, `>` could break sc.exe's argument parsing. Admin-gated via `ElevationCheck`, so exploitation requires admin — hence low severity.
- **Suggestion:** Reject shell metacharacters in `VerifyPathSafe`, or build via `ProcessStartInfo.ArgumentList` where sc.exe permits.

### WebSocket endpoints don't validate `Origin` (severity: low)
- **Location:** `WebSocketVoiceEndpoints.cs:30-37`, `WebSocketTextEndpoints.cs:32-39`, `WebSocketTerminalEndpoints.cs:33-47`.
- **Issue:** No check of `Sec-WebSocket-Origin`. Today auth is via `api_key` query param and a 192-bit random key — a cross-origin page can't forge it. But combined with the token-in-URL smell, any leak (referrer, browser extension) is followed by a successful cross-origin WS.
- **Suggestion:** When deployed behind a known `BaseUrl`, validate `Origin`. Low priority given bearer-token auth.

### Dev API key committed to source (severity: low)
- **Location:** `appsettings.Development.json:9` — `"ApiKey": "dev-api-key-change-me"`; used by tests at `ApiKeyAuthTests.cs:12`.
- **Issue:** Literal dev key in the repo. Only a real risk if `appsettings.Development.json` ever gets bundled into the publish output (today `CopyToPublishDirectory` is kept Never). Also a trap for a dev running a local build without noticing.
- **Suggestion:** Pin the test expectation via `dotnet user-secrets` in development; add a test asserting `appsettings.Development.json` is not in the publish output.

### `ExpandTool` doesn't scope messages by conversation (severity: low)
- **Location:** `ExpandToolHandler.cs:38-57`; `SqliteConversationStore.GetMessagesByIds` at `SqliteConversationStore.cs:372-392`.
- **Issue:** The tool fetches messages by ID without gating on the current `conversationId`. If `message_ids` are predictable or leaked, the LLM in conversation A can retrieve messages from conversation B.
- **Suggestion:** Filter by the `conversationId` passed to `ExecuteAsync`: `store.GetMessagesByIds(args.MessageIds, conversationId)` and add a `WHERE ConversationId = @cid` clause.

## Open Questions
- **Single shared API key for all surfaces** (REST, WebSocket, admin, webhook-trigger) — acceptable for a single-user self-hosted agent, but the key grants full file + shell access. Do we want per-role keys (read-only vs shell-enabled), or should the pluggable-auth comment in `ApiKeyServiceExtensions.cs:9` graduate to Entra ID / GitHub OAuth before any remote user gains access?
- **Path-scope helper** — extract one canonical `PathScope.IsInside(root, candidate)` into `OpenAgent.Contracts`, or accept that every tool re-implements the check (with the prefix bug) and fix each site in place?
- **Redirect policy for `web_fetch`** — follow with re-validation (max 5 hops), or reject 3xx outright? Many legit sites use 3xx.
- **DNS rebinding / ConnectCallback** — CLAUDE.md says this is accepted. The fix is cheap once we're already rewriting the HttpClient handler for the redirect issue; worth revisiting.
- **Secrets encryption at rest** — config dir is reachable via the file-explorer API under the same API key. Worth DPAPI / file-mode hardening, or do we decide "if you have the API key you have everything, so don't bother"?
- **Token-in-URL boot ritual** — is there appetite for a one-shot `/auth/exchange` that swaps the fragment for an HttpOnly cookie and clears the hash?

## Files reviewed
- `src/agent/OpenAgent.Security.ApiKey/ApiKeyAuthenticationHandler.cs`
- `src/agent/OpenAgent.Security.ApiKey/ApiKeyAuthenticationOptions.cs`
- `src/agent/OpenAgent.Security.ApiKey/ApiKeyResolver.cs`
- `src/agent/OpenAgent.Security.ApiKey/ApiKeyServiceExtensions.cs`
- `src/agent/OpenAgent/AgentConfigConfigurable.cs`
- `src/agent/OpenAgent/Program.cs`
- `src/agent/OpenAgent/RootResolver.cs`
- `src/agent/OpenAgent/DataDirectoryBootstrap.cs`
- `src/agent/OpenAgent/SystemPromptEndpoints.cs`
- `src/agent/OpenAgent/SystemPromptBuilder.cs`
- `src/agent/OpenAgent/Installer/InstallerCli.cs`
- `src/agent/OpenAgent/Installer/DirectoryLinkCreator.cs`
- `src/agent/OpenAgent/Installer/ServiceInstaller.cs`
- `src/agent/OpenAgent/Installer/SystemCommandRunner.cs`
- `src/agent/OpenAgent/Installer/PreInstallChecks.cs`
- `src/agent/OpenAgent/Installer/FirewallRule.cs`
- `src/agent/OpenAgent.Tools.WebFetch/UrlValidator.cs`
- `src/agent/OpenAgent.Tools.WebFetch/IDnsResolver.cs`
- `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs`
- `src/agent/OpenAgent.Tools.WebFetch/WebFetchToolHandler.cs`
- `src/agent/OpenAgent.Tools.WebFetch/ContentExtractor.cs`
- `src/agent/OpenAgent.Tools.FileSystem/FileSystemToolHandler.cs`
- `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs`
- `src/agent/OpenAgent.Tools.FileSystem/FileWriteTool.cs`
- `src/agent/OpenAgent.Tools.FileSystem/FileAppendTool.cs`
- `src/agent/OpenAgent.Tools.FileSystem/FileEditTool.cs`
- `src/agent/OpenAgent.Tools.FileSystem/SymlinkRoots.cs`
- `src/agent/OpenAgent.Tools.Shell/ShellToolHandler.cs`
- `src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs`
- `src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs`
- `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/ConversationEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/ConnectionEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/FileExplorerEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/LogEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/AdminEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/ScheduledTaskEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/ToolEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/WebSocketTextEndpoints.cs`
- `src/agent/OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs`
- `src/agent/OpenAgent.ConfigStore.File/FileConfigStore.cs`
- `src/agent/OpenAgent.ConfigStore.File/FileConnectionStore.cs`
- `src/agent/OpenAgent.Models/Configs/AgentConfig.cs`
- `src/agent/OpenAgent.Models/Configs/GlobalConfig.cs`
- `src/agent/OpenAgent.Skills/SkillDiscovery.cs`
- `src/agent/OpenAgent.Skills/SkillToolHandler.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramWebhookEndpoints.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs`
- `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppEndpoints.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`
- `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs`
- `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- `src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs`
- `.gitignore`
- `src/agent/OpenAgent/appsettings.Development.json`
- `src/agent/OpenAgent.Tests/WebFetch/UrlValidatorTests.cs`
- `src/agent/OpenAgent.Tests/FileSystemErrorMessageTests.cs`
