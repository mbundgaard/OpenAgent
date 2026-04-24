# Windows Service Deployment — Design Spec

Issue: TBD | Depends on: nothing | Blocks: nothing

---

## Purpose

Run OpenAgent as a native Windows service on a dedicated Windows machine (e.g. a home Plex/media server) where the agent needs full filesystem and process access to the host — not the sandboxed, single-`dataPath` model used in the Linux Docker deployment.

The agent's job in this environment is to orchestrate media automation end-to-end: query an external torrent indexer, submit magnets to a cloud download service, move completed files from cloud storage to local drives, rename and organize them for Plex, and trigger library scans. That requires reach *outside* the current data directory without breaking the sandbox model that the Docker deployment relies on.

This spec covers deployment (single self-installing executable), runtime layout (one folder, symlinks for large media volumes), and the small generalizations to existing bootstrap/config code needed to support it. The Linux Docker deployment continues to work unchanged.

---

## Goals

- Ship a single self-contained `OpenAgent.exe` that doubles as its own installer (`--install` / `--uninstall` / `--restart` / `--status`).
- Run as a Windows service (`LocalSystem`), auto-start at boot, survive reboots.
- Give the agent reach outside its own directory via user-configured symlinks (e.g. `D:\Media`) without a new path syntax or multi-root gate.
- Keep the Linux Docker deployment working without touching the Dockerfile or the `DATA_DIR` env var.
- Expose the existing web UI on the LAN so the user can manage the agent from a phone.

## Non-goals

- MSI installer, code signing, winget distribution. A raw `.exe` is sufficient for a single-user home server.
- Multi-instance orchestration. One service per machine.
- UAC self-elevation for install/uninstall. Fail fast with a clear message instead.
- Multi-root path abstraction with prefix syntax (`media:foo/bar`). Rejected in favor of the symlink approach.
- Windows-specific publish profile that trims unused providers/channels. YAGNI — ship the full artifact.
- Sandboxing the Shell tool against the symlink layout. Shell exec is unsandboxed by design; document it.

---

## Command Surface

One executable, behavior selected at startup:

| Invocation | Behavior |
|---|---|
| `OpenAgent.exe` | Console mode (dev / run-in-foreground). |
| `OpenAgent.exe --service` | Service mode. Entry point the Service Control Manager calls after install. |
| `OpenAgent.exe --install` | Copies self to install path, registers service, starts it. Requires admin. |
| `OpenAgent.exe --install --path "E:\OpenAgent"` | Install to a non-default path. |
| `OpenAgent.exe --install --open-firewall-port` | Additionally open the bound HTTP port in Windows Firewall. Default off. |
| `OpenAgent.exe --uninstall` | Stops service, removes registration, deletes install folder's binary. Does **not** delete config/logs/db. Requires admin. |
| `OpenAgent.exe --restart` | Stops then starts the service. Requires admin. |
| `OpenAgent.exe --status` | Prints service state (installed? running? install path? root path?). No admin required. |

Mode selection lives at the top of `Program.cs`. Non-service modes short-circuit before building the web host and return immediately after their action.

### Elevation

`--install`, `--uninstall`, `--restart` require admin. Detect via `new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)`. If not elevated, print a clear message pointing at "Run as Administrator" and exit with a non-zero code. No silent self-relaunch.

### Install mechanics

Install and uninstall shell out to `sc.exe` rather than P/Invoke the Service Control Manager. `binPath=` must wrap the quoted exe path so paths with spaces survive:

- `sc create OpenAgent binPath= "\"<install path>\OpenAgent.exe\" --service" start= auto DisplayName= "OpenAgent"`
- `sc description OpenAgent "Multi-channel AI agent platform"`
- `sc failure OpenAgent reset= 86400 actions= restart/5000/restart/5000/restart/60000` — first two restarts after 5 s, third after 60 s, failure counter resets after 24 h. Without this, the SCM leaves the service stopped on unhandled exception, defeating the point of Always On.
- `sc start OpenAgent`
- `sc stop OpenAgent` + `sc delete OpenAgent` for uninstall.
- For path changes during reinstall (see Upgrade Lifecycle): `sc config OpenAgent binPath= "\"<new path>\OpenAgent.exe\" --service"`.

Idempotent: if `--install` runs against an existing registration, stop the service, overwrite the binary at the registered path, `sc config` if `--path` changed, restart. No separate `--upgrade` verb.

### Pre-install checks

Before copying anything, `--install` validates its source folder and target:

- `node\baileys-bridge.js` exists in the source folder. Missing is a hard failure — refuse the install with a clear message pointing at the extraction step ("expected node\\baileys-bridge.js next to the exe; extract the full distribution before running --install"). WhatsApp is part of the target workflow on this deployment.
- `node --version` returns successfully (Node.js 22+ on `PATH`). Missing is a hard failure with a clear message and a link to nodejs.org. Rationale: easier to diagnose at install time than at first WhatsApp-connection start hours later.
- `--path` doesn't contain characters `sc.exe` can't quote (null, line breaks). Spaces are fine given the quote-escaping above; refuse exotic paths with a clear error.

---

## Service Identity + Install Path

**Service account:** `LocalSystem`. No password. Full access to local drives. Cannot access UNC/mapped network shares the way a logged-in user can — acceptable, the target use case is a single Windows box with local volumes.

If the user later needs a domain account, the shape is easy to add: `--install --user DOMAIN\user` prompts for a password and passes it to `sc create`. Not building it now.

**Default install path:** `C:\OpenAgent\`. Not `%ProgramFiles%`.

Rationale: `%ProgramFiles%` is conventionally for immutable binaries, state goes in `%ProgramData%`. For this deployment, state and binary live in the same folder (see Root Directory section). `C:\OpenAgent\` is the convention for self-hosted apps on Windows, sidesteps UAC theatre when the user interacts with the exe outside the service context, and makes "the whole folder is a single backup unit" literally true.

Override with `--install --path "E:\OpenAgent"`.

---

## Root Directory Model

**Keep the existing `DATA_DIR` env var. Add a fallback to the exe directory when it's unset.**

The host reads its root from:

1. `DATA_DIR` environment variable if set — existing behavior, unchanged. The Linux Docker image continues to set `DATA_DIR=/home/data`; Azure deployments continue to read it; no existing deployment breaks.
2. Otherwise `AppContext.BaseDirectory` — the folder containing the running exe. This is the new fallback path used by the Windows service, which does **not** set `DATA_DIR`.

No separate data directory concept for the Windows deployment. The service's install folder *is* its state folder.

### Layout on Windows

```
C:\OpenAgent\
    OpenAgent.exe
    node\                    (WhatsApp Baileys bridge + node_modules)
    config\
        agent.json
        connections.json
    logs\                    (Serilog JSONL — log-YYYY-MM-DD.jsonl)
    conversations.db
    memory.db                (once issue #17 lands)
    projects\
    repos\
    memory\
    skills\
    connections\
    media -> D:\Media        (directory junction created from agent.json)
    downloads -> E:\Downloads
```

### Why not a multi-root abstraction

An earlier draft proposed a multi-root allowlist (`{ data: ..., media: ..., downloads: ... }`) with a `media:foo/bar` path prefix. Rejected for three reasons:

- Prefix syntax imposes a tax on every file tool call and doesn't match what shell output gives the agent (shell reports `D:\Media\...`, tools would want `media:...`).
- Absolute-path allowlists break cross-platform parity — prompt content has to change between Linux and Windows deployments.
- The symlink approach is implementable in a few lines of existing bootstrap code, with zero changes to file tools.

### Symlinks via `config/agent.json`

```json
{
  "symlinks": {
    "media":     "D:\\Media",
    "downloads": "E:\\Downloads"
  }
}
```

On startup, `DataDirectoryBootstrap` reads `symlinks` and creates directory junctions at `<root>\<name>` targeting the configured paths:

- If nothing exists at the junction path, create the junction. On Windows, shell out to `cmd /c mklink /J "<root>\<name>" "<target>"` (see Open Questions for the API choice). On Linux, use `Directory.CreateSymbolicLink` for a symlink.
- If a junction (or symlink on Linux) already exists at the path and points at the configured target, skip silently.
- If it exists but points elsewhere, log a warning and leave it untouched. Do not silently repoint.
- If a regular directory or file exists at the path, log a warning and skip.

### Why junctions (not symlinks) on Windows

- Junctions don't require admin or Developer Mode; `LocalSystem` creates them without issue.
- Transparent to path normalization: `Path.GetFullPath("media/TV/foo.mkv", root)` resolves to `C:\OpenAgent\media\TV\foo.mkv`, which the existing single-root gate accepts. The OS resolves the junction only at I/O time.
- Rename/move within `media\` stays on the target volume (metadata op, no copy). Moving between `downloads\` and `media\` is still a cross-volume copy — intrinsic to the volumes being separate, not an artifact of the design.

### Existing single-root gate stays unchanged

The gate currently implemented for `dataPath` continues to work: resolve the path with `Path.GetFullPath`, verify it starts with the root + directory separator, using `OrdinalIgnoreCase` comparison on Windows. Junction traversal happens under the OS at I/O time, outside the gate's visibility.

When the gate rejects a path, the error message must hint at the root-relative convention so the model can self-correct: "Path must be relative to the agent's root directory. For media files, use `media/…`; for downloads, `downloads/…`." Without this, the model seeing `D:\Media\foo.mkv` rejected will loop on variations of the absolute path instead of switching conventions.

### Symlink changes require service restart

`DataDirectoryBootstrap` reads `symlinks` from `agent.json` only during its `Run()` pass at startup. Manual edits to `agent.json` (or future UI-driven edits) do not take effect until the service restarts. Call this out in any UI that writes to `symlinks`. The junction-creation logic should be factored into a reusable helper so a future endpoint can invoke it idempotently without a full restart if that becomes desirable — but that's a later extension, not part of this spec's scope.

### Agent-side path convention

The agent uses paths relative to the root:

- `config/agent.json`
- `media/TV/Severance/Season 02/S02E01.mkv`
- `downloads/completed/show.mkv`

Shell tool output will surface real paths (`D:\Media\TV\...`) because downstream tools like qBittorrent and Plex don't know about the junction. The system prompt must state the convention explicitly: "when passing paths to file tools, translate `D:\Media\*` to `media/*` (and `E:\Downloads\*` to `downloads/*`)." Trivial rule, LLM-friendly.

### Dev override

Running from `bin\Debug\net10.0\` would treat the build output as the root — undesirable. Set `DATA_DIR=C:\dev\openagent-data\` (or similar) in the developer's environment or `launchSettings.json`. The env var check runs first; the production Windows service leaves it unset and falls through to `AppContext.BaseDirectory`.

### Cross-platform parity

Nothing changes on Linux. The existing Docker image sets `DATA_DIR=/home/data`; the host reads it as it always has. The Dockerfile is **not** touched by this spec. Only new behavior: when `DATA_DIR` is unset (which only happens on the Windows service deployment), the host falls back to the exe's own directory.

---

## Binary Layout + Node Bridge

Built via `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`. Expected size ~70–90 MB with trimming enabled.

The single-file publish cannot reasonably bundle the Node.js Baileys `node_modules` tree inside the exe. The bridge ships as a sibling folder:

```
C:\OpenAgent\
    OpenAgent.exe
    node\
        baileys-bridge.js
        node_modules\
        package.json
```

`--install` copies the entire install folder (exe + `node\` sibling) to the target path, not just the exe. The source folder for the copy is the directory containing the invoked exe — standard "drop the extracted zip somewhere, run `OpenAgent.exe --install`" flow.

WhatsApp is in scope for this deployment, so the `node\` folder is required, not optional. `--install` hard-fails without it (see Pre-install checks).

**Runtime prerequisite:** Node.js 22+ on `PATH`. Verified at `--install` time and refused cleanly if missing. No auto-install of Node from the internet.

---

## Upgrade Lifecycle

Flow: user downloads a new build (zip or loose files), runs `OpenAgent.exe --install` from the download location. Idempotent behavior:

1. Detect existing `OpenAgent` service registration via `sc query`.
2. If present: stop the service, wait for stopped state, replace the binary (and `node\` folder) at the registered install path, update registration if the user passed a new `--path`, start the service.
3. If absent: copy files to `--path` (default `C:\OpenAgent\`), register the service, start it.

Config, logs, database, media junctions, skills, and projects are preserved across upgrades — all live under the install folder, nothing is wiped by the copy-over-top step. Files the upgrade genuinely replaces (the exe and `node\` contents) don't carry user state.

**Embedded default personality files are not re-extracted on upgrade.** `DataDirectoryBootstrap` only writes a default if the file is missing. This is deliberate — user customizations to AGENTS.md, SOUL.md, IDENTITY.md, USER.md, TOOLS.md, VOICE.md, or MEMORY.md are preserved — but it means improvements we ship to the *defaults* in a later build don't reach users who already have the file. Users who want new defaults must delete the specific file before `--install` (or diff and merge manually). Acceptable trade-off; document it in release notes when defaults change meaningfully.

Graceful shutdown: the SCM default stop timeout is ~30 seconds (`ServicesPipeTimeout`). When a stop is issued, the host triggers its `IHostApplicationLifetime` cancellation. Long-running LLM streams, shell tool calls, and file operations must respect the cancellation token or the SCM will hard-kill the process, truncating in-flight work. The Shell tool already has a timeout/kill path; LLM providers already honor the cancellation token on their HTTP calls — verify both during implementation.

`--uninstall` stops the service, deregisters it, deletes the exe and `node\` folder. **Does not delete** `config\`, `logs\`, `conversations.db`, `memory.db`, `projects\`, `repos\`, `memory\`, `skills\`, `connections\`, or any symlinks. Reinstalling picks up the existing state unchanged.

---

## Logging

**Keep** existing Serilog JSONL at `<root>\logs\log-YYYY-MM-DD.jsonl`. No change. The `/api/logs` endpoint continues to read from there.

**Add** Windows Event Log writes for lifecycle events only:

- Service start
- Service stop
- Service crash (unhandled exception at host level)
- Install / uninstall / restart actions
- Bootstrap failures (root unwritable, symlink creation failed, etc.)

Event source name: `OpenAgent`. Source registration happens during `--install` (admin, which we already have). Event Log is precious — do not write per-request or per-tool-call entries. The point is "did the service come up cleanly?" visibility in `eventvwr.msc`, not full log routing.

Implementation: `Microsoft.Extensions.Logging.EventLog` provider wired into the host only in service mode (`args.Contains("--service")`).

---

## Remote Access

Bind Kestrel to `http://+:8080` in service mode so LAN clients (including phones) can reach the web UI. Port is configurable via `appsettings.json` or an environment variable.

**Firewall:** opt-in via `--install --open-firewall-port`. Default off. When the flag is present, the installer runs `netsh advfirewall firewall add rule name="OpenAgent" dir=in action=allow protocol=TCP localport=<port>`. `--uninstall` removes the rule by name.

**Remote access beyond the LAN** (phone from outside the home network): user's choice of Tailscale, Cloudflare Tunnel, or port forwarding. Out of scope for this spec — the agent doesn't bundle or configure any of them.

---

## First-run Behavior

Root resolution lives in `Program.cs` (line 32 today). The existing `DATA_DIR`-wins logic is unchanged; only the hardcoded fallback `/home/data` shifts to `AppContext.BaseDirectory` so the Windows service works without setting an env var.

`DataDirectoryBootstrap.Run()` already handles folder creation and default personality file extraction. Extensions for this deployment:

1. The default `agent.json` content changes from `"{}"` to `"{\"symlinks\": {}}"` so the shape is discoverable to users editing the file or UI code reading it.
2. After creating subfolders and extracting embedded defaults, read `config/agent.json`'s `symlinks` block and create junctions accordingly.
3. Junction failures are logged as warnings; bootstrap continues. The agent is still usable without the configured symlinks — it just can't reach the target paths.

Everything else — extracting AGENTS.md, SOUL.md, IDENTITY.md, etc., writing `connections.json` — stays unchanged.

---

## Code Changes (High-level)

| File | Change |
|---|---|
| `OpenAgent/Program.cs` | (1) Root resolution — change line 32's fallback from the literal `/home/data` to `AppContext.BaseDirectory` so the Windows service works without setting `DATA_DIR`. The env-var-wins semantics are unchanged. (2) Parse `--install` / `--uninstall` / `--restart` / `--status` / `--service` before `WebApplication.CreateBuilder(args)`. Admin check for admin-requiring modes. Shell out to `sc.exe` for install/uninstall/restart. (3) `AddWindowsService()` when `--service` is present. |
| `OpenAgent/DataDirectoryBootstrap.cs` | (1) Change the default `agent.json` content from `"{}"` to `"{\"symlinks\": {}}"` so the shape is discoverable. (2) After the existing folder + defaults extraction, read the `symlinks` block from `config/agent.json` and create directory junctions (Windows, via `cmd /c mklink /J`) or symlinks (Linux, via `Directory.CreateSymbolicLink`). No root-resolution logic — the resolved `dataPath` is still passed in from `Program.cs`. |
| `OpenAgent/Installer/` (new) | `ServiceInstaller.cs`, `FirewallRule.cs`, `ElevationCheck.cs`, `EventLogSource.cs`. |
| `OpenAgent.csproj` | `RuntimeIdentifiers` adds `win-x64`. Package reference to `Microsoft.Extensions.Hosting.WindowsServices` and `Microsoft.Extensions.Logging.EventLog`. |
| `appsettings.json` | No change. Kestrel keeps existing binding on port 8080. |
| `Dockerfile` | **Not touched.** Existing `DATA_DIR=/home/data` stays. |

New tests:

- `DataDirectoryBootstrapTests` — symlink creation idempotency, "points elsewhere, skip with warning" behavior, default `agent.json` seeds `{"symlinks": {}}`.
- `RootResolutionTests` (Program.cs logic) — `DATA_DIR` set takes precedence; unset falls back to `AppContext.BaseDirectory`. Extract the resolution into a small testable helper if it stays inline in `Program.cs` today.
- `ServiceInstallerTests` — verb parsing, admin-check behavior (with a mock elevation checker), `sc.exe` argument composition.
- Path gate — existing tests still pass unchanged (gate logic doesn't change).

---

## Decisions Locked

- **Junction creation:** shell out to `cmd /c mklink /J "<link>" "<target>"` via `Process.Start`. Simpler than P/Invoking `DeviceIoControl`, and `LocalSystem` can run it without elevation issues.
- **Default HTTP port:** 8080, matching the container. Configurable via `appsettings.json` for users with port conflicts.
- **Trim unused providers/channels:** no. Ship the full artifact. Single-digit MB savings not worth a second build profile.
- **Service account `--user` flag:** deferred entirely. Not part of this spec. If the need arises, a follow-up spec defines the prompt/password handling.

## Open Questions

None remaining at spec time. Any surprises surface during implementation and feed back into this document.

---

## Failure Modes

How the service behaves when things go wrong:

| Failure | Behavior | Recovery |
|---|---|---|
| **Service won't start** (unhandled exception at host build time, e.g. malformed `appsettings.json`) | Windows Event Log receives a service-start-failed entry from the Event Log provider. SCM marks the service as failed. | User opens `eventvwr.msc` → Application log → filter by source `OpenAgent`. Fix config, `sc start OpenAgent`. The `sc failure` recovery actions do **not** trigger on start failures, only on crashes of a running service — this is correct, we don't want infinite restart loops on bad config. |
| **Symlink target volume offline at boot** (external drive not mounted yet, e.g. `D:\` is removable) | `DataDirectoryBootstrap` fails to create the junction (target path doesn't exist). Logs a warning, continues startup. The agent runs but tool calls into `media/*` fail with a clear filesystem error. | User brings the volume online, `--restart` the service to re-run bootstrap. |
| **Junction exists but target volume goes offline at runtime** | File tool calls fail with `DirectoryNotFoundException` / `IOException`. Gate still accepts the path; OS rejects the I/O. | Error surfaces to the agent, which can surface it to the user. No automatic remediation. |
| **HTTP port already in use** | Kestrel bind fails at startup, host crashes with a bound-address-in-use exception. Event Log records the crash. | User edits `appsettings.json` (or sets `ASPNETCORE_URLS`) to a free port, `--restart`. Document this in the release notes / troubleshooting section. |
| **Node.js missing at runtime** (user uninstalled Node after install, or PATH changed) | `WhatsAppChannelProvider` fails to spawn the bridge on its next start attempt. Logged to JSONL; connection-status UI shows the connection as failed. Rest of the agent keeps running. | User reinstalls Node, `--restart` (or stop/start the WhatsApp connection from the UI). |
| **Disk full on the root volume** | SQLite writes fail, log writes fail. Agent still answers requests that don't hit disk; anything requiring persistence raises exceptions. Event Log entry on first write failure. | User frees space, `--restart` to recover any failed-write state. |
| **Unhandled exception during running service** (e.g. tool handler throws an uncaught exception that bubbles past the host) | SCM applies `sc failure` recovery — restarts after 5 s, again after 5 s, third time after 60 s. Failure counter resets after 24 h. Event Log records each crash. | If crashes keep happening, user investigates logs. The 60-s gap on third restart gives the user a window to disable the service if needed. |
| **Port-forward / firewall misconfiguration** | Service binds and runs; external clients get connection refused. Not detectable from inside the service. | User problem, outside the service's control. Release notes point at `--install --open-firewall-port` and the Tailscale/Cloudflare options. |
| **SCM hard-kill during long-running tool call** | If a stop takes longer than `ServicesPipeTimeout` (~30 s default), SCM kills the process. In-flight work is lost; SQLite is WAL-mode so no corruption, but any tool output captured in memory is gone. | Mitigation: cancellation tokens must be honored everywhere. If this becomes a real issue, users can raise `ServicesPipeTimeout` via registry. Not shipping registry tweaks in the installer. |

This list is the failure-mode contract for implementation. Tests should cover the deterministic ones (missing symlink target, port in use, disk full simulated via mock filesystem).
