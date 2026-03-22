# Web Terminal — Implementation Plan

## Overview

Add a browser-based terminal to the web portal using xterm.js on the frontend and Linux PTY via P/Invoke on the backend. No sidecar processes, no NuGet PTY packages.

**Important**: We do NOT use `forkpty()` because calling `fork()` from managed .NET code corrupts the CLR (only the calling thread survives). Instead we use `posix_openpt` + `grantpt` + `unlockpt` + `ptsname` to create the PTY pair, then launch bash via `System.Diagnostics.Process` with shell redirects to the slave device.

## Architecture

```
Browser (xterm.js)  <──WebSocket──>  WebSocketTerminalEndpoints  <──>  TerminalSessionManager
                                                                              │
                                                                       PtyTerminalSession
                                                                              │
                                                                  posix_openpt() ──> master fd
                                                                  Process.Start("bash") ──> slave PTY
```

- **Binary frames**: keystrokes (client→server) and PTY output (server→client)
- **Text frames**: JSON control messages (resize)
- Same dual read/write loop pattern as `WebSocketVoiceEndpoints`

---

## Step 1: Contracts — `ITerminalSession` + `ITerminalSessionManager`

**Files:**
- `src/agent/OpenAgent.Contracts/ITerminalSession.cs`
- `src/agent/OpenAgent.Contracts/ITerminalSessionManager.cs`

```csharp
// ITerminalSession.cs
namespace OpenAgent.Contracts;

/// <summary>
/// A live PTY terminal session. Write keystrokes in, read output out.
/// </summary>
public interface ITerminalSession : IAsyncDisposable
{
    /// <summary>Writes raw bytes (keystrokes) to the PTY stdin.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Reads PTY output as it arrives.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct);

    /// <summary>Resizes the PTY window.</summary>
    void Resize(int cols, int rows);
}
```

```csharp
// ITerminalSessionManager.cs
namespace OpenAgent.Contracts;

/// <summary>
/// Manages terminal session lifecycle — create, retrieve, close.
/// </summary>
public interface ITerminalSessionManager
{
    ITerminalSession Create(string sessionId, string workingDirectory);
    ITerminalSession? Get(string sessionId);
    Task CloseAsync(string sessionId);
}
```

---

## Step 2: New project — `OpenAgent.Terminal`

**Location:** `src/agent/OpenAgent.Terminal/`

### 2a: `Native/PtyInterop.cs` — P/Invoke declarations

P/Invoke into libc for PTY allocation and I/O:

```csharp
using System.Runtime.InteropServices;

namespace OpenAgent.Terminal.Native;

/// <summary>
/// P/Invoke bindings for Linux PTY operations (forkpty, read, write, ioctl).
/// </summary>
internal static partial class PtyInterop
{
    // forkpty: allocates a PTY pair, forks, and returns the master fd
    // Returns child PID in parent, 0 in child
    [LibraryImport("libutil.so.1", SetLastError = true)]
    internal static partial int forkpty(
        out int masterFd,
        nint name,         // null — we don't need the slave name
        nint termp,        // null — default terminal settings
        ref Winsize winsize);

    // Read from master fd (PTY output)
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial nint read(int fd, ref byte buf, nint count);

    // Write to master fd (keystrokes)
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial nint write(int fd, ref byte buf, nint count);

    // Close fd
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial int close(int fd);

    // Resize PTY
    // TIOCSWINSZ = 0x5414 on Linux
    [LibraryImport("libc.so.6", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int ioctl_winsize(int fd, uint request, ref Winsize winsize);

    // exec shell in child process
    [LibraryImport("libc.so.6", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int execvp(string file, string?[] argv);

    // chdir in child process
    [LibraryImport("libc.so.6", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int chdir(string path);

    // waitpid to reap zombie
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial int waitpid(int pid, out int status, int options);

    // kill child process
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial int kill(int pid, int signal);

    internal const uint TIOCSWINSZ = 0x5414;
    internal const int WNOHANG = 1;
    internal const int SIGTERM = 15;
    internal const int SIGKILL = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }
}
```

### 2b: `PtyTerminalSession.cs` — PTY lifecycle + I/O

Core logic:
1. `forkpty()` — parent gets master fd + child PID; child calls `chdir` then `execvp("/bin/bash")`
2. Background read loop: `read(masterFd)` → pushes chunks into a `Channel<byte[]>`
3. `Write()` — `write(masterFd, data)` for keystrokes
4. `Resize()` — `ioctl(masterFd, TIOCSWINSZ, winsize)`
5. `DisposeAsync()` — `kill(pid, SIGTERM)`, `close(masterFd)`, `waitpid(pid)`

```csharp
namespace OpenAgent.Terminal;

public sealed class PtyTerminalSession : ITerminalSession, IAsyncDisposable
{
    private readonly int _masterFd;
    private readonly int _childPid;
    private readonly Channel<byte[]> _outputChannel;
    private readonly Task _readLoopTask;
    private readonly CancellationTokenSource _cts = new();

    // Constructor: calls forkpty, starts read loop
    // ReadOutputAsync: yields from _outputChannel
    // Write: write() to masterFd
    // Resize: ioctl TIOCSWINSZ
    // DisposeAsync: SIGTERM → waitpid → close fd
}
```

Key details:
- Read buffer size: 4096 bytes (typical terminal chunk)
- `Channel<byte[]>` is unbounded — PTY output is bursty but bounded by terminal speed
- The child process inherits no open file descriptors except the PTY slave
- Set `TERM=xterm-256color` env var in child before exec

### 2c: `TerminalSessionManager.cs`

Follows `VoiceSessionManager` pattern:

```csharp
namespace OpenAgent.Terminal;

public sealed class TerminalSessionManager : ITerminalSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ITerminalSession> _sessions = new();
    private const int MaxSessions = 4;

    // Create: enforce MaxSessions, create PtyTerminalSession, add to dict
    // Get: lookup by sessionId
    // CloseAsync: remove + dispose
    // DisposeAsync: close all
}
```

### 2d: Project file — `OpenAgent.Terminal.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
  </ItemGroup>
</Project>
```

No NuGet dependencies — only P/Invoke and framework types.

---

## Step 3: WebSocket Endpoint — `WebSocketTerminalEndpoints.cs`

**File:** `src/agent/OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs`

Route: `/ws/terminal/{sessionId}`

Pattern mirrors `WebSocketVoiceEndpoints`:

```
MapWebSocketTerminalEndpoints()
  ├── Validate WebSocket request
  ├── sessionManager.Create(sessionId, workingDirectory) or .Get(sessionId)
  ├── AcceptWebSocketAsync()
  └── RunBridgeAsync(ws, session, ct)
        ├── ReadLoop: ws.ReceiveAsync → text frames = JSON control, binary frames = session.Write(keystrokes)
        └── WriteLoop: session.ReadOutputAsync → ws.SendAsync(binary frames)
```

Control messages (text frames, client→server):
```json
{"type": "resize", "cols": 120, "rows": 40}
```

Data flow (binary frames):
- Client→Server: raw keystrokes (what xterm.js sends on `terminal.onData`)
- Server→Client: raw PTY output (fed directly to `terminal.write()`)

The endpoint creates the terminal session on first connect. Working directory = `AgentEnvironment.DataPath`.

---

## Step 4: DI wiring in `Program.cs`

Add to `Program.cs`:

```csharp
using OpenAgent.Terminal;

builder.Services.AddSingleton<ITerminalSessionManager, TerminalSessionManager>();

// After app.Build():
app.MapWebSocketTerminalEndpoints();
```

Add project reference from `OpenAgent` → `OpenAgent.Terminal`.

---

## Step 5: Frontend — Terminal app with xterm.js

### 5a: Install xterm.js

```bash
cd src/web && npm install xterm @xterm/addon-fit @xterm/addon-webgl
```

### 5b: `src/web/src/apps/terminal/TerminalApp.tsx`

```tsx
export function TerminalApp() {
  const termRef = useRef<HTMLDivElement>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const terminalRef = useRef<Terminal | null>(null);

  useEffect(() => {
    // 1. Create Terminal instance with theme matching the portal dark theme
    // 2. Load FitAddon, WebglAddon
    // 3. Open terminal in termRef div
    // 4. Connect WebSocket to /ws/terminal/{sessionId}?api_key={token}
    // 5. terminal.onData → ws.send(binary)     — keystrokes out
    // 6. ws.onmessage → terminal.write(data)   — PTY output in
    // 7. fitAddon.fit() on mount + ResizeObserver
    // 8. On resize → send JSON {"type":"resize","cols":N,"rows":N} as text frame
    // 9. Cleanup: close ws, dispose terminal
  }, []);

  return <div ref={termRef} style={{ width: '100%', height: '100%' }} />;
}
```

Key frontend details:
- `sessionId` = `crypto.randomUUID()` per app instance (new terminal per window)
- xterm.js theme colors to match the portal's dark theme
- `FitAddon` auto-sizes to the window; `ResizeObserver` on the container re-fits + sends resize control message
- Binary WebSocket frames for data, text frames for control

### 5c: `src/web/src/apps/terminal/TerminalApp.module.css`

Minimal — just ensure the xterm container fills the window and has no padding conflicts.

### 5d: Register in `registry.ts`

```typescript
import { TerminalApp } from './terminal/TerminalApp';

{
  id: 'terminal',
  title: 'Terminal',
  icon: 'terminal-icon',
  component: TerminalApp,
  defaultSize: { width: 800, height: 500 },
}
```

### 5e: Add terminal icon to taskbar

Follow the existing icon pattern (CSS-based icons in the taskbar).

---

## Step 6: Solution file + project references

- Add `OpenAgent.Terminal` to `OpenAgent.sln`
- Add project reference: `OpenAgent` → `OpenAgent.Terminal`
- Add project reference: `OpenAgent.Api` needs no direct ref (endpoint resolves `ITerminalSessionManager` from DI)

---

## Commit sequence

1. **Contracts**: `ITerminalSession`, `ITerminalSessionManager`
2. **OpenAgent.Terminal**: P/Invoke, `PtyTerminalSession`, `TerminalSessionManager`, csproj
3. **Endpoint**: `WebSocketTerminalEndpoints.cs`
4. **DI wiring**: `Program.cs` changes, solution/project references
5. **Frontend**: xterm.js install, `TerminalApp`, CSS, registry

---

## Security

- Terminal sessions scoped to `dataPath` as working directory
- Max 4 concurrent sessions (configurable)
- Same API key auth as all other endpoints
- No root access — runs as the app's service account
- Idle timeout: close sessions with no I/O for 10 minutes (future enhancement)

## Not in scope (future)

- Windows support (PTY on Windows needs ConPTY — different API)
- Session reconnection / persistence across page reload
- Idle timeout with automatic cleanup
- Terminal scrollback limit configuration
