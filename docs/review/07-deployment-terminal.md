# Deployment & Terminal Review — 2026-04-23

## TL;DR

The Windows installer is small, layered, and tested — shell-outs go through an abstraction, pre-install checks exist, and the service-install story is correct in happy path. However exit codes from `sc.exe` / `netsh` are silently discarded, so the installer cheerfully reports success on partial failures. The bigger risk sits in the Terminal domain: `WebSocketTerminalEndpoints` exposes a raw interactive shell (bash on Linux, cmd on Windows) over a WebSocket. When deployed as a Windows service, that shell runs as `LocalSystem` and is reachable via `X-Api-Key` header *or* `?api_key=` query string, with no Origin check, no CSRF mitigation, no idle timeout, and no session-closure on WS drop. A leaked key on a LAN-exposed deployment is effectively remote SYSTEM. Additional issues: a lifecycle bug where the *first* connection's `finally` block can evict the *second* connection's CTS registration; the `wwwroot` extraction performs a full delete-and-recreate on every cold start (disk churn + a narrow window where static files 404); and service `Start`/`Stop`/`Delete`/`Create` never check exit codes so the installer can report success without the service being registered.

## Strengths

- **Thin installer abstraction.** `ISystemCommandRunner` + `FakeSystemCommandRunner` let `ServiceInstallerTests`, `FirewallRuleTests`, and `DirectoryLinkCreatorTests` verify exact command strings. Composition is covered end-to-end without touching the real `sc.exe`.
- **Pre-install checks are grouped and fail fast.** `InstallerCli.Install` evaluates all three checks and reports the first failure with actionable messaging (`src/agent/OpenAgent/Installer/InstallerCli.cs:61-73`).
- **Cross-platform link abstraction.** `DirectoryLinkCreator` cleanly splits Windows (junction via `cmd /c mklink /J`) and Linux (`Directory.CreateSymbolicLink`). Junctions avoid the admin-or-DeveloperMode symlink tax on Windows.
- **Bootstrap is genuinely idempotent.** `DataDirectoryBootstrap.Run` never overwrites existing markdown, re-reads `agent.json`, handles missing / regular-directory / wrong-target-symlink cases with explicit warnings rather than crashes (`DataDirectoryBootstrap.cs:105-129`).
- **Session-manager concurrency.** `TerminalSessionManager.Create` uses a `SemaphoreSlim` plus check-then-add so two racing `Create` calls with the same `sessionId` return the same session rather than creating two bash processes (`TerminalSessionManager.cs:29-44`).
- **PTY allocation is serialised.** `PtyTerminalSession` holds a static lock across `posix_openpt` + `grantpt` + `unlockpt` + `ptsname` because `ptsname` returns a static buffer (`PtyTerminalSession.cs:39-62`) — good.
- **fd cleanup on process-start failure.** `PtyTerminalSession` closes the master fd if `Process.Start` throws (`PtyTerminalSession.cs:95-100`).
- **Single-consumer guard on WebSocket.** `ActiveBridges` cancels and evicts any prior WebSocket for the same `sessionId` so two browsers don't fight over a single PTY (`WebSocketTerminalEndpoints.cs:51-56`).

## Bugs

### Terminal WebSocket is a remote shell (severity: critical)

- **Location:** `src/agent/OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs:33-110`, `src/agent/OpenAgent.Terminal/ProcessTerminalSession.cs:29-45`, `src/agent/OpenAgent.Security.ApiKey/ApiKeyAuthenticationHandler.cs:26-33`.
- **Issue:** `/ws/terminal/{sessionId}` accepts a WebSocket and bridges raw bytes to `cmd.exe /Q` (Windows) or `bash -i` (Linux) with `WorkingDirectory = environment.DataPath` but **no privilege drop, no process sandbox, no command filter**. Authentication is API-key-based with two acceptance modes: `X-Api-Key` header *or* `api_key` query string (added specifically because "WebSocket clients can't set headers" per the handler comment). The WebSocket handshake does not verify `Origin`, does not check `Sec-Fetch-Site`, and there is no CSRF token — in the default React flow the key is pasted into `window.location.hash` and stored in `localStorage`. Put that together with:
  - **Windows service mode runs as `LocalSystem`** (`InstallerCli.cs:88`, service created by `sc create` with no `obj=` override, default account is `LocalSystem`).
  - **String comparison is non-constant-time** (`ApiKeyAuthenticationHandler.cs:40`, `string.Equals(..., StringComparison.Ordinal)` short-circuits on first mismatch — already tracked as S-4.1 in `review-by-opus-high.md`).
  - **The installer offers `--open-firewall-port`** that adds a netsh rule opening TCP 8080 to `dir=in action=allow` on all profiles, including the public profile (`FirewallRule.cs:13-15`). If the operator also sets `ASPNETCORE_URLS` to `http://*:8080` or `http://0.0.0.0:8080` (common pattern for LAN access, the Dockerfile already does this), the terminal endpoint is LAN-reachable as SYSTEM.
- **Risk:** A leaked or guessed API key (guessable via timing side-channel per S-4.1) yields remote-interactive SYSTEM shell on the host. Cross-origin attack: a browser tab visiting a malicious page while the user is logged in to `http://localhost:8080/#token=...` — the malicious page can `new WebSocket("ws://localhost:8080/ws/terminal/evil?api_key=" + knownKey)` without same-origin restriction because there's no Origin check on WS upgrade. Bookmarklet / XSS via the chat UI also reach the same endpoint.
- **Fix (layered, all needed):**
  1. **Add Origin check** to the WS endpoint — reject upgrades whose `Origin` header is not the configured UI origin. ASP.NET Core: `WebSocketOptions.AllowedOrigins` in `UseWebSockets`, or manually inspect `context.Request.Headers.Origin` before calling `AcceptWebSocketAsync`.
  2. **Remove `?api_key=` query-string auth for the terminal endpoint** — use the `Sec-WebSocket-Protocol` header sub-protocol trick for token passing (the one browser can set on WS) or require a short-lived ticket from a prior authenticated `POST /api/terminal/tickets` call.
  3. **Switch service account to `NT SERVICE\OpenAgent` (virtual service account)** in `ServiceInstaller.Create` — add `obj= "NT SERVICE\OpenAgent"` to the `sc create` line. This gives the service an automatically-managed low-privilege identity and removes the SYSTEM blast radius.
  4. **Make the terminal endpoint opt-in in config** (`AgentConfig.TerminalEnabled`, default `false` in service mode). Console mode can default-true because it runs as the interactive user anyway.
  5. Fix S-4.1 (constant-time compare) in parallel — already tracked.

### WebSocket drop never closes the terminal session (severity: high)

- **Location:** `src/agent/OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs:95-109`.
- **Issue:** The `finally` block of the handler removes the entry from `ActiveBridges` and closes the `WebSocket`, but **never calls `sessionManager.CloseAsync(sessionId)`**. The `ITerminalSession` (bash/cmd process + IO channel + reader thread) stays alive in `TerminalSessionManager._sessions` indefinitely. `TerminalSessionManager` also has no idle sweep — once four orphaned sessions accumulate, `Create` throws `InvalidOperationException: Maximum terminal sessions (4) reached` and the whole terminal feature is dead until the host is restarted.
- **Risk:** Denial of service against the terminal feature; zombie child processes (bash / cmd) survive until host restart; on Windows, conhost / cmd processes linger as SYSTEM.
- **Fix:** Already tracked as TT-2.1. Either (a) on bridge-finally call `sessionManager.CloseAsync(sessionId)` so disconnect = terminate (simpler, matches REST habit), or (b) track "last active" per session and add a hosted service that sweeps idle sessions after N minutes.

### ActiveBridges finally can evict the wrong registration (severity: high)

- **Location:** `src/agent/OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs:77-98`.
- **Issue:** Already tracked as EP-5.1. Sequence: connection A stores its CTS at `ActiveBridges[sid]`. Connection B starts, `TryRemove(sid, out previousCts)` retrieves A's CTS, cancels and disposes it, then `ActiveBridges[sid] = bridgeCts` stores B's. A's bridge exits via cancellation and enters its `finally`, which does `ActiveBridges.TryRemove(sessionId, out _)` unconditionally — this removes **B's** registration. Future connection C finds nothing to evict, so B and C both race on the same PTY's output channel.
- **Risk:** Two concurrent consumers reading from the same `ReadOutputAsync` channel; output appears torn between windows; `B.Dispose` may be called out-of-order. Worst-case data corruption of terminal output, not privilege escalation.
- **Fix:** Use `ConcurrentDictionary<TKey, TValue>`'s `ICollection<KVP>.Remove(new KVP(sessionId, bridgeCts))` — that overload only removes if both key and value match. Or `if (ActiveBridges.TryGetValue(sessionId, out var cur) && ReferenceEquals(cur, bridgeCts)) ActiveBridges.TryRemove(sessionId, out _);`.

### sc.exe / netsh exit codes silently ignored (severity: high)

- **Location:** `src/agent/OpenAgent/Installer/ServiceInstaller.cs:15-31`, `src/agent/OpenAgent/Installer/FirewallRule.cs:13-19`.
- **Issue:** `Create`, `Start`, `Stop`, `Delete`, `UpdateBinPath` on `ServiceInstaller` and `Add`, `Remove` on `FirewallRule` all call `_runner.Run(...)` and discard the returned `CommandResult`. If `sc create` fails (invalid args, permission denied after UAC drop, an orphaned service entry with a handle open, etc.), `InstallerCli.Install` proceeds to `EventLogRegistrar.Ensure()`, `FirewallRule.Add()`, `installer.Start(...)`, and finally prints `"OpenAgent registered as Windows service..."` with exit 0. The user believes the install worked; nothing is registered; no error is surfaced.
- **Risk:** Operator-confusing partial failures. The worst case is a user who typed `--install`, saw a success message, and walks away — their agent never runs. Also hides upgrade bugs where `sc config` fails after a `sc stop`, leaving the service wedged in a bad state.
- **Fix:** Make `Run(string args)` in `ServiceInstaller` throw on non-zero unless the caller opts out. Pattern:
  ```csharp
  private void RunOrThrow(string verb, string args) {
      var r = _runner.Run("sc.exe", args);
      if (r.ExitCode != 0)
          throw new InvalidOperationException($"sc.exe {verb} failed (exit {r.ExitCode}): {r.Output}");
  }
  ```
  Accept a "best-effort" mode for `Stop` (stopping an already-stopped service returns 1062). `FirewallRule.Remove` should tolerate "rule not present" as success.

### wwwroot extraction deletes then recreates on every startup (severity: medium)

- **Location:** `src/agent/OpenAgent/Program.cs:50-69`.
- **Issue:** On every cold start, `ExtractEmbeddedWwwroot` does `Directory.Delete(wwwrootPath, recursive: true)` then `CreateDirectory` then `archive.ExtractToDirectory(..., overwriteFiles: true)`. Consequences:
  1. **Service restart window:** between `Delete` and the last file being extracted, `/` returns 404 for any cached-chunk request the browser makes. React chunk-loading (`Failed to fetch dynamically imported module`) will trip if a user was mid-interaction during a service restart.
  2. **Disk churn:** every restart writes hundreds of KB of React assets back to disk. Fine on SSD, bad on underpowered Azure App Service.
  3. **Crash mid-extract** (OOM, process kill, antivirus hold) leaves an empty or half-populated `wwwroot/`. Next start re-extracts, so recoverable — but the service is serving 404s until then.
  4. **Two processes racing** — if `--install`'s `installer.Start` kicks off the service just as a stale console-mode process is shutting down in the same folder, both extraction attempts collide (`Directory.Delete` on a directory another process has open fails). Unlikely in practice given the install flow, but the lock file would make this more robust.
- **Risk:** UX degradation during restart windows, especially during `--restart` or automatic failure recovery (the installer's `sc failure` config restarts the service within 5 s of a crash — a browser open during that window experiences broken state).
- **Fix:**
  - Hash the embedded zip stream (e.g., compute the `ManifestResourceInfo` size + CRC, or hash the first few bytes) and store it at `wwwroot/.embedded.hash`. Skip extraction if unchanged.
  - Extract to `wwwroot.new/`, then atomically `Directory.Move` to `wwwroot/` (and `Directory.Delete` the old one). Keeps the old `wwwroot/` serving until the new one is fully written.
  - Extract to a versioned folder (`wwwroot-{assemblyInfoVersion}`) and point a `wwwroot` junction at it — gives rollback.
  - Move this logic into a separate class (`OpenAgent/Startup/WwwrootExtractor.cs`) with a unit test. It is currently inlined at the top of `Program.cs` with no test coverage.

### Firewall rule hardcodes port 8080 but bound port is configurable (severity: medium)

- **Location:** `src/agent/OpenAgent/Installer/InstallerCli.cs:15,86`, `src/agent/OpenAgent/Program.cs:46`.
- **Issue:** `InstallerCli.DefaultHttpPort = 8080` is hardcoded and passed to `FirewallRule.Add(ServiceName, DefaultHttpPort)`. Meanwhile the Kestrel bind is `Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8080"`. If the operator sets `ASPNETCORE_URLS=http://*:9000`, the service listens on 9000 but the firewall rule opens 8080. Traffic is silently dropped.
- **Risk:** Operator confusion; misleading `--install --open-firewall-port` semantics.
- **Fix:** Either (a) parse `ASPNETCORE_URLS` (or read `AgentConfig.BindUrl`) and derive the port for the firewall rule, or (b) accept `--port <N>` on `--install` and use it for both the firewall rule and a `SETX` of `ASPNETCORE_URLS`.

### FirewallRule.Add can create duplicate rules (severity: low)

- **Location:** `src/agent/OpenAgent/Installer/FirewallRule.cs:13`.
- **Issue:** `netsh advfirewall firewall add rule name="OpenAgent" ...` is not idempotent — each call adds another rule with the same name. A repeated `--install` (after failed `--uninstall`) accumulates rules. `delete rule name="OpenAgent"` removes *all* matching, so cleanup still works, but the "list rules" UX on the user's firewall panel gets noisy.
- **Risk:** Cosmetic + occasional confusion.
- **Fix:** Call `Remove` before `Add`, or use `netsh ... show rule name="OpenAgent"` to skip if present. Ideally switch to the native Windows Firewall COM interface (`INetFwPolicy2`) which has proper add-or-update semantics.

### Uninstall does not remove the EventLog source (severity: low)

- **Location:** `src/agent/OpenAgent/Installer/InstallerCli.cs:94-122`, `src/agent/OpenAgent/Installer/EventLogRegistrar.cs`.
- **Issue:** `Uninstall` stops + deletes the service and removes the firewall rule, but leaves the `OpenAgent` source registered under the `Application` log (`EventLog.DeleteEventSource` is never called). A subsequent fresh install is tolerant because `SourceExists` short-circuits, but the registry stays dirty.
- **Risk:** Registry leftover; no functional problem.
- **Fix:** Add `EventLogRegistrar.Remove()` calling `if (EventLog.SourceExists(SourceName)) EventLog.DeleteEventSource(SourceName);` and invoke from `Uninstall` *before* `ServiceInstaller.Delete` (deleting the source while the service is running produces confusing errors).

### ioctl signature mismatches Linux `unsigned long` (severity: low)

- **Location:** `src/agent/OpenAgent.Terminal/Native/PtyInterop.cs:47-48`.
- **Issue:** Linux `ioctl(int fd, unsigned long request, ...)` — `request` is `unsigned long` (8 bytes on 64-bit). The P/Invoke binding uses `uint request` (4 bytes). On x86-64, 32-bit `uint` is passed in `edi`; the upper 32 bits of `rdi` are left zero-extended by the processor, so `TIOCSWINSZ = 0x5414` fits and works. But the signature is wrong — strictly-conforming kernels on other architectures may read garbage; some ABIs expect a full 8-byte push.
- **Risk:** Today: none in practice (x86-64 Linux zeros the upper bits). Future: ARM64 Linux (Azure App Service has ARM64 offerings now), cross-compiled musl builds, mac if you ever port — may produce garbled `TIOCSWINSZ` argument.
- **Fix:** `internal static partial int ioctl_winsize(int fd, nuint request, ref Winsize winsize);` and `internal const nuint TIOCSWINSZ = 0x5414;`.

### PtyInterop P/Invoke comments out handle-close on failure paths (severity: low)

- **Location:** `src/agent/OpenAgent.Terminal/PtyTerminalSession.cs:179-182`.
- **Issue:** In `DisposeAsync`, `PtyInterop.close(_masterFd)` runs after the bash process is already killed. But if the `_masterFd` is already-closed (shouldn't happen, but defensive), `close()` returns `-1/EBADF` silently. No leak, just a noisy errno. Acceptable.
- **Risk:** None today. Noted as a smell.
- **Fix:** None necessary.

## Smells

### Duplicated service-name strings (severity: low)

- **Location:** `src/agent/OpenAgent/Installer/InstallerCli.cs:12-14` defines the constants but `src/agent/OpenAgent/Installer/EventLogRegistrar.cs:14-15` redefines `SourceName = "OpenAgent"` and `LogName = "Application"`. `InstallerCli.ServiceName` and `EventLogRegistrar.SourceName` happen to both equal `"OpenAgent"`. Drift risk is low but real — a future rename would need to touch both.
- **Suggestion:** Consolidate into one `ServiceIdentity` record:
  ```csharp
  public static class ServiceIdentity {
      public const string Name = "OpenAgent";
      public const string DisplayName = "OpenAgent";
      public const string Description = "Multi-channel AI agent platform";
      public const string EventLogSource = Name;
      public const string EventLogName = "Application";
  }
  ```

### Shelling out to sc.exe / netsh instead of native APIs (severity: medium)

- **Location:** `src/agent/OpenAgent/Installer/ServiceInstaller.cs`, `src/agent/OpenAgent/Installer/FirewallRule.cs`, `src/agent/OpenAgent/Installer/DirectoryLinkCreator.cs`.
- **Issue:** All three wrap command-line tools: `sc.exe`, `netsh.exe`, `cmd /c mklink /J`. Each layer introduces quoting, parsing, localisation (e.g., `sc query` output strings depend on OS language), and exit-code fidelity concerns.
- **Suggestion:** Windows offers native APIs:
  - **SCM:** `System.ServiceProcess.ServiceController` + P/Invoke to `advapi32.dll` `CreateService` / `ChangeServiceConfig2` / `ChangeServiceConfig` / `DeleteService` for install and failure-action config.
  - **Firewall:** `INetFwPolicy2` COM interface — proper add-or-update, query by name, idempotent.
  - **Junction:** `DeviceIoControl(FSCTL_SET_REPARSE_POINT, ...)` — the .NET `Directory.CreateSymbolicLink` with a junction flag would be the cleanest, but the API exposes only true symlinks. P/Invoke is reasonable here.
  
  This is deferred work; current approach is pragmatic and testable. But once exit-code checking is added (see bug above), the net complexity shifts in favour of native APIs.

### wwwroot extraction logic lives in Program.cs (severity: low)

- **Location:** `src/agent/OpenAgent/Program.cs:50-69`.
- **Issue:** `ExtractEmbeddedWwwroot` is a static local in `Program.cs` with no test coverage. It handles error cases by writing to `Console.Error` (not Serilog — Serilog isn't initialised yet at this point). It references `AppContext.BaseDirectory` directly which is hard to override in tests.
- **Suggestion:** Extract to `OpenAgent/Startup/WwwrootExtractor.cs` with a constructor taking `(string targetPath, Stream zipStream, ILogger logger)` and cover with unit tests for: (a) zip present, clean target, extracts; (b) zip present, existing content, overwrites; (c) no zip resource, no-op; (d) extraction throws mid-stream, leaves clean state.

### No rollback on multi-step install failure (severity: medium)

- **Location:** `src/agent/OpenAgent/Installer/InstallerCli.cs:82-92`.
- **Issue:** `Install` does: create service → register event log → (optional) add firewall rule → start service. If `Start` throws or returns non-zero, the service, event-log source, and firewall rule all stay. User must `--uninstall` to reset. No attempt at structured rollback.
- **Suggestion:** Wrap each step in a try/finally stack so failure at step N rolls back steps 0..N-1. Only relevant once exit codes are actually checked (see `sc.exe / netsh exit codes silently ignored`).

### Running as `LocalSystem` by default (severity: high)

- **Location:** `src/agent/OpenAgent/Installer/ServiceInstaller.cs:18` (the `sc create` line doesn't specify `obj=`, so Windows defaults to `LocalSystem`).
- **Issue:** `LocalSystem` is the highest local-trust account on Windows. Anything the agent does — file writes, tool invocations, terminal shells, curl fetches from `shell_exec` — runs with full system authority. Combined with the terminal WebSocket, that's a remote SYSTEM risk (see critical bug).
- **Suggestion:** Use a virtual service account (`NT SERVICE\OpenAgent`) or create a dedicated local user at install time. Virtual service accounts need no password management, appear in ACLs, and lose dangerous privileges (`SeTcbPrivilege`, `SeDebugPrivilege`) compared to SYSTEM. Add `obj= "NT SERVICE\\OpenAgent"` to the `sc create` args and grant `FullControl` to the install folder and `AppContext.BaseDirectory` (the service account is auto-created on first service start).

### `VerifyPathSafe` is a surrogate for real validation (severity: low)

- **Location:** `src/agent/OpenAgent/Installer/PreInstallChecks.cs:35-43`.
- **Issue:** `VerifyPathSafe` checks for `\0`, `\n`, `\r` in the install path. It does *not* validate length (MAX_PATH traps on older Windows), does not validate disk-existence, does not reject UNC paths (`\\server\share\OpenAgent.exe` might work or fail under service account). It reads like a half-finished filter.
- **Suggestion:** Either drop (the quoting in `ServiceInstaller.Create` handles spaces; NTFS rejects `"`, `:`, `*`, `?`, `<`, `>`, `|` at creation time so a real-world installed exe path can't contain them) — or expand it to cover actual edge cases (path length, UNC vs local, mounted-volume check). The current middle ground adds noise with no security benefit.

### Session-manager logs the wrong "Platform" on Windows (severity: low)

- **Location:** `src/agent/OpenAgent.Terminal/TerminalSessionManager.cs:42-44,95`.
- **Issue:** `LogInformation("Terminal session created: {SessionId} ({Platform})", sessionId, RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "PTY" : "Process")` — this is fine. But on macOS or FreeBSD, the code branches to `ProcessTerminalSession` (since `IsLinux` is false) — which on macOS would spawn `cmd.exe` (line 31 isWindows false → no, wait, line 31 chooses `isWindows ? cmd.exe : /bin/bash`). So on macOS: `ProcessTerminalSession` is used with `/bin/bash`. That's actually fine. The "PTY or Process" label is a bit coarse but not wrong.
- **Suggestion:** Log `RuntimeInformation.RuntimeIdentifier` or `OS` alongside the "PTY"/"Process" label.

### `ProcessTerminalSession` reimplements terminal echo manually (severity: medium)

- **Location:** `src/agent/OpenAgent.Terminal/ProcessTerminalSession.cs:61-109`.
- **Issue:** Windows doesn't have conpty bindings here, so the session falls back to redirected stdin/stdout and does manual character echo, backspace handling (`_inputLength` bounds), `\r` → `\r\n` rewriting, etc. This will subtly break interactive programs (passwords will echo, `readline`-based CLIs like `python` REPL will behave wrongly, cursor-positioning apps won't work at all). The comment at file top acknowledges this ("No true terminal emulation").
- **Suggestion:** On Windows, use conpty via `CreatePseudoConsole` (available since Win10 1809) — `Microsoft.Terminal.Wpf` and `winpty-agent` already exist; the Windows SDK headers are `processenv.h` / `consoleapi.h`. The existing `PtyInterop` pattern is the right shape to copy, but for Windows. This isn't trivial (it's a non-trivial P/Invoke layer), but it's the right long-term path.

### Terminal shell is rooted at `environment.DataPath`, not a sandboxed chroot (severity: medium)

- **Location:** `src/agent/OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs:62-63`, `src/agent/OpenAgent.Terminal/PtyTerminalSession.cs:79-88`, `src/agent/OpenAgent.Terminal/ProcessTerminalSession.cs:33-42`.
- **Issue:** The working directory is `environment.DataPath`, but nothing prevents the user from typing `cd C:\Windows\System32` (or on Linux `cd /`). File tools (`FileReadTool`, etc.) enforce `StartsWith(basePath)` scoping, but the Terminal session intentionally doesn't — it *is* an unrestricted shell. Combined with `LocalSystem`, a mistyped-or-malicious command touches the whole machine.
- **Suggestion:** If the terminal stays, at minimum (a) chroot-style restriction on Linux (not trivial — `chroot(2)` needs root), (b) on Windows use `CreateProcessWithRestrictedToken` to drop privileges below the service account, or (c) disable the terminal feature in service mode entirely (console mode only).

### `cmd /c mklink /J` is shell-injection-shaped (severity: low)

- **Location:** `src/agent/OpenAgent/Installer/DirectoryLinkCreator.cs:23`.
- **Issue:** `$"/c mklink /J \"{linkPath}\" \"{targetPath}\""` — `cmd` parses `&`, `|`, `^`, `%`, `>`, `<`. If `targetPath` legitimately contains `&` (NTFS allows it in directory names), it goes inside the quoted block and cmd treats it literally — *inside quotes*. So the common attacker-controlled cases are blocked by NTFS' own filename rules: `"`, `<`, `>`, `|`, `:`, `*`, `?` are all disallowed in filenames, and `"` would be the only one that breaks the quoting. For `%` inside quotes, cmd does still do variable expansion (`%PATH%` expands inside double quotes). A path like `D:\Data\%OS%\target` would get `%OS%` expanded before the junction is created — wrong target.
- **Risk:** Symlink misdirection, not RCE. The bootstrap checks `Directory.Exists(target)` before calling `CreateLink`, so unexpanded targets that fail the existence check would be skipped. Existence check uses the literal path (before cmd expansion) while mklink uses the cmd-expanded version — they can disagree if the literal and expanded both happen to exist. Edge case.
- **Suggestion:** Bypass cmd entirely. On .NET 6+, `Directory.CreateSymbolicLink` creates a true symlink but not a junction. For junction creation without cmd, use `DeviceIoControl(FSCTL_SET_REPARSE_POINT, ...)` P/Invoke — more code, zero injection surface.

## Open Questions

- **What's the intended deployment mode for service-mode terminals?** If the terminal is only ever used via the console-mode React UI by the installing user, the LAN exposure under `--open-firewall-port` is a dormant foot-gun. Consider disabling the terminal endpoint unless explicitly enabled.
- **Why is `--service` routed via `args.Contains("--service")` (not the `TryHandle` switch)?** A future `--service --debug` would still be routed correctly, but centralising the install-mode dispatch would be cleaner — `TryHandle` could return a record `(handled, exitCodeOrHost)` rather than `int?`.
- **Is there an upgrade story?** The CLAUDE.md mentions "Upgrade flow: stop service, replace files, start service." But `--install` hard-refuses when the service exists ("Service OpenAgent is already registered. Use --uninstall first..."). Should there be `--upgrade` that does stop → replace-safe files → `UpdateBinPath` → start?
- **Does the Windows installer need an MSI eventually?** Current `--install` is elegant for devs but harder for non-technical users. MSI would cover uninstall entries in Add/Remove Programs and upgrade codes.
- **Should the embedded wwwroot be content-addressed rather than zip-replaced?** If the assets are `/assets/index-{hash}.js`, a strict content-hash approach means never having to rewrite them — just drop the new ones next to the old.
- **Conpty on Windows: scheduled?** The current `ProcessTerminalSession` is honest about its limitations. If interactive programs (vim, python REPL, less) are on the roadmap, conpty is on the critical path.
- **Is `MaxSessions = 4` chosen, or arbitrary?** No documentation or rationale for why 4. A comment explaining the intended use (one PTY per top-level tab × 2 user concurrent, with headroom) would help future tuning.

## Files reviewed

- `src/agent/OpenAgent/Installer/InstallerCli.cs`
- `src/agent/OpenAgent/Installer/ServiceInstaller.cs`
- `src/agent/OpenAgent/Installer/FirewallRule.cs`
- `src/agent/OpenAgent/Installer/ElevationCheck.cs`
- `src/agent/OpenAgent/Installer/EventLogRegistrar.cs`
- `src/agent/OpenAgent/Installer/PreInstallChecks.cs`
- `src/agent/OpenAgent/Installer/DirectoryLinkCreator.cs`
- `src/agent/OpenAgent/Installer/IDirectoryLinkCreator.cs`
- `src/agent/OpenAgent/Installer/SystemCommandRunner.cs`
- `src/agent/OpenAgent/Installer/ISystemCommandRunner.cs`
- `src/agent/OpenAgent/Program.cs` (installer dispatch, wwwroot extraction, service-mode wiring)
- `src/agent/OpenAgent/RootResolver.cs`
- `src/agent/OpenAgent/DataDirectoryBootstrap.cs`
- `src/agent/OpenAgent/OpenAgent.csproj`
- `scripts/publish-windows.ps1`
- `src/agent/OpenAgent.Terminal/Native/PtyInterop.cs`
- `src/agent/OpenAgent.Terminal/ProcessTerminalSession.cs`
- `src/agent/OpenAgent.Terminal/PtyTerminalSession.cs`
- `src/agent/OpenAgent.Terminal/TerminalSessionManager.cs`
- `src/agent/OpenAgent.Terminal/OpenAgent.Terminal.csproj`
- `src/agent/OpenAgent.Contracts/ITerminalSession.cs`
- `src/agent/OpenAgent.Contracts/ITerminalSessionManager.cs`
- `src/agent/OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs`
- `src/agent/OpenAgent.Security.ApiKey/ApiKeyAuthenticationHandler.cs` (cross-reference for terminal auth)
- `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs`, `FileWriteTool.cs`, `FileAppendTool.cs`, `FileEditTool.cs`, `FileSystemToolHandler.cs` (cross-reference for SYSTEM-scope concerns)
- `src/agent/OpenAgent.Tests/Installer/ServiceInstallerTests.cs`
- `src/agent/OpenAgent.Tests/Installer/FirewallRuleTests.cs`
- `src/agent/OpenAgent.Tests/Installer/PreInstallChecksTests.cs`
- `src/agent/OpenAgent.Tests/Installer/DirectoryLinkCreatorTests.cs`
- `src/agent/OpenAgent.Tests/Installer/FakeSystemCommandRunner.cs`
- `src/agent/OpenAgent.Tests/RootResolverTests.cs`
- `src/agent/OpenAgent.Tests/DataDirectoryBootstrapTests.cs`
- `docs/review/review-by-opus-high.md` (cross-reference for existing findings S-4.1, S-5.1, TT-2.1, EP-5.1)
