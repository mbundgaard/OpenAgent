# OpenClaw Bash Tool — Reference Implementation

This document describes how OpenClaw implements its `bash` tool for shell command execution. This serves as a reference for implementing a similar tool in Open Agent.

## Overview

The bash tool executes shell commands and returns stdout/stderr output. It handles long-running commands, output truncation, timeouts, and cross-platform shell detection.

**Source:** OpenClaw uses the implementation from `@mariozechner/pi-coding-agent`:
- `bash.ts` — main tool implementation
- `truncate.ts` — output truncation utilities
- `shell.ts` (in utils) — shell detection and process management

## Tool Definition

```typescript
{
  name: "bash",
  description: "Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to last 2000 lines or 50KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.",
  parameters: {
    command: string,           // Bash command to execute
    timeout?: number           // Timeout in seconds (optional)
  }
}
```

## Execution Flow

### 1. Shell Detection

OpenClaw automatically finds the appropriate shell:

**Resolution order:**
1. User-specified `shellPath` in settings
2. On Windows: Git Bash in known locations, then bash on PATH
3. On Unix: `/bin/bash`, then bash on PATH, then fallback to `sh`

```typescript
function getShellConfig(): { shell: string; args: string[] } {
  // Check user-specified shell path first
  if (customShellPath && existsSync(customShellPath)) {
    return { shell: customShellPath, args: ["-c"] };
  }
  
  if (process.platform === "win32") {
    // Try Git Bash in Program Files
    const gitBashPaths = [
      `${process.env.ProgramFiles}\\Git\\bin\\bash.exe`,
      `${process.env["ProgramFiles(x86)"]}\\Git\\bin\\bash.exe`,
    ];
    for (const path of gitBashPaths) {
      if (existsSync(path)) return { shell: path, args: ["-c"] };
    }
    // Fallback: search PATH for bash.exe
    const bashOnPath = findBashOnPath();
    if (bashOnPath) return { shell: bashOnPath, args: ["-c"] };
    throw new Error("No bash shell found");
  }
  
  // Unix: /bin/bash → bash on PATH → sh
  if (existsSync("/bin/bash")) return { shell: "/bin/bash", args: ["-c"] };
  const bashOnPath = findBashOnPath();
  if (bashOnPath) return { shell: bashOnPath, args: ["-c"] };
  return { shell: "sh", args: ["-c"] };
}
```

### 2. Working Directory Check

```typescript
if (!existsSync(cwd)) {
  reject(new Error(`Working directory does not exist: ${cwd}\nCannot execute bash commands.`));
  return;
}
```

Verifies the working directory exists before spawning.

### 3. Command Execution

```typescript
const child = spawn(shell, [...args, command], {
  cwd,
  detached: true,          // Enables process group for cleanup
  env: getShellEnv(),      // Augmented PATH
  stdio: ["ignore", "pipe", "pipe"],  // stdin ignored, stdout/stderr piped
});
```

**Key spawn options:**
- `detached: true` — Allows killing the entire process tree
- `stdio: ["ignore", "pipe", "pipe"]` — No stdin, capture stdout/stderr
- `env` — Custom environment with augmented PATH

### 4. Output Streaming and Buffering

Output is streamed in real-time while also being buffered for the final response:

```typescript
const chunks: Buffer[] = [];
let chunksBytes = 0;
const maxChunksBytes = DEFAULT_MAX_BYTES * 2;  // Keep 2x for truncation

const handleData = (data: Buffer) => {
  totalBytes += data.length;
  
  // Start temp file if output exceeds threshold
  if (totalBytes > DEFAULT_MAX_BYTES && !tempFilePath) {
    tempFilePath = getTempFilePath();
    tempFileStream = createWriteStream(tempFilePath);
    // Write buffered chunks to file
    for (const chunk of chunks) {
      tempFileStream.write(chunk);
    }
  }
  
  // Continue writing to temp file
  if (tempFileStream) {
    tempFileStream.write(data);
  }
  
  // Keep rolling buffer of recent data
  chunks.push(data);
  chunksBytes += data.length;
  
  // Trim old chunks if buffer too large
  while (chunksBytes > maxChunksBytes && chunks.length > 1) {
    const removed = chunks.shift()!;
    chunksBytes -= removed.length;
  }
  
  // Stream partial output to callback
  if (onUpdate) {
    const truncation = truncateTail(Buffer.concat(chunks).toString("utf-8"));
    onUpdate({ content: [{ type: "text", text: truncation.content }] });
  }
};

child.stdout.on("data", handleData);
child.stderr.on("data", handleData);
```

**Key behaviors:**
- Stdout and stderr are merged into a single stream
- Rolling buffer keeps the most recent output
- Large output spills to temp file
- Real-time streaming via `onUpdate` callback

### 5. Temp File for Large Output

```typescript
function getTempFilePath(): string {
  const id = randomBytes(8).toString("hex");
  return join(tmpdir(), `pi-bash-${id}.log`);
}
```

When output exceeds ~50KB, full output is written to a temp file so the model can read it if needed.

### 6. Output Truncation (Tail)

For command output, tail truncation is used (keep the **end**, not the beginning):

```typescript
const DEFAULT_MAX_LINES = 2000;
const DEFAULT_MAX_BYTES = 50 * 1024;  // 50KB

function truncateTail(content: string): TruncationResult {
  const lines = content.split("\n");
  
  // No truncation needed?
  if (lines.length <= maxLines && totalBytes <= maxBytes) {
    return { content, truncated: false };
  }
  
  // Work backwards from the end
  const outputLines: string[] = [];
  let outputBytes = 0;
  
  for (let i = lines.length - 1; i >= 0 && outputLines.length < maxLines; i--) {
    const line = lines[i];
    const lineBytes = Buffer.byteLength(line, "utf-8") + (outputLines.length > 0 ? 1 : 0);
    
    if (outputBytes + lineBytes > maxBytes) {
      // Edge case: last line alone exceeds limit — take partial
      if (outputLines.length === 0) {
        const truncatedLine = truncateStringToBytesFromEnd(line, maxBytes);
        outputLines.unshift(truncatedLine);
        lastLinePartial = true;
      }
      break;
    }
    
    outputLines.unshift(line);
    outputBytes += lineBytes;
  }
  
  return {
    content: outputLines.join("\n"),
    truncated: true,
    truncatedBy: /* "lines" or "bytes" */,
    totalLines,
    outputLines: outputLines.length,
    // ...
  };
}
```

**Why tail truncation for bash?**
- Errors usually appear at the end
- Build/compile output: final status at the end
- Command results at the end
- Beginning often has verbose setup/progress

### 7. Timeout Handling

```typescript
let timeoutHandle: NodeJS.Timeout | undefined;
if (timeout !== undefined && timeout > 0) {
  timeoutHandle = setTimeout(() => {
    timedOut = true;
    if (child.pid) {
      killProcessTree(child.pid);
    }
  }, timeout * 1000);
}

// On completion
if (timeoutHandle) clearTimeout(timeoutHandle);

// On close
if (timedOut) {
  reject(new Error(`timeout:${timeout}`));
}
```

When timeout expires:
1. Set timedOut flag
2. Kill entire process tree
3. Reject with timeout error (includes any output received)

### 8. Process Tree Killing

```typescript
function killProcessTree(pid: number): void {
  if (process.platform === "win32") {
    // Use taskkill on Windows
    spawn("taskkill", ["/F", "/T", "/PID", String(pid)], {
      stdio: "ignore",
      detached: true,
    });
  } else {
    // Use SIGKILL on Unix — kill process group
    try {
      process.kill(-pid, "SIGKILL");  // Negative PID = process group
    } catch {
      // Fallback to killing just the process
      process.kill(pid, "SIGKILL");
    }
  }
}
```

**Why process tree killing?**
- Commands may spawn child processes (npm, make, docker, etc.)
- Killing just the shell leaves orphaned children
- `detached: true` + negative PID kills the entire group on Unix
- `taskkill /T` kills the tree on Windows

### 9. Exit Code Handling

```typescript
child.on("close", (code) => {
  // ...
  if (exitCode !== 0 && exitCode !== null) {
    outputText += `\n\nCommand exited with code ${exitCode}`;
    reject(new Error(outputText));
  } else {
    resolve({ content: [{ type: "text", text: outputText }] });
  }
});
```

- Exit code 0: Success
- Non-zero exit code: Reject with error (includes output)
- Output is included in both success and error cases

### 10. Abort Handling

```typescript
const onAbort = () => {
  if (child.pid) {
    killProcessTree(child.pid);
  }
};

if (signal) {
  if (signal.aborted) {
    onAbort();
  } else {
    signal.addEventListener("abort", onAbort, { once: true });
  }
}
```

AbortSignal triggers process tree kill.

## Environment Augmentation

```typescript
function getShellEnv(): NodeJS.ProcessEnv {
  const binDir = getBinDir();
  const pathKey = Object.keys(process.env).find(
    (key) => key.toLowerCase() === "path"
  ) ?? "PATH";
  
  const currentPath = process.env[pathKey] ?? "";
  const pathEntries = currentPath.split(delimiter).filter(Boolean);
  const hasBinDir = pathEntries.includes(binDir);
  
  return {
    ...process.env,
    [pathKey]: hasBinDir ? currentPath : [binDir, currentPath].join(delimiter),
  };
}
```

Adds the tool's bin directory to PATH so installed utilities are available.

## Output Formatting

Final output includes truncation info when applicable:

```
[actual output here]

[Showing lines 1850-2000 of 5432. Full output: /tmp/pi-bash-a8f3c2d1.log]
```

Or for byte truncation:

```
[output]

[Showing lines 1-2000 of 2000 (50.0KB limit). Full output: /tmp/pi-bash-a8f3c2d1.log]
```

## Error Cases

| Error | Cause |
|-------|-------|
| `Working directory does not exist` | CWD doesn't exist |
| `Command exited with code N` | Non-zero exit |
| `Command timed out after N seconds` | Timeout exceeded |
| `Command aborted` | Cancelled via AbortSignal |
| `No bash shell found` | Windows without Git Bash/Cygwin |

## Design Decisions

1. **Tail truncation:** Keep the end of output, not the beginning. Errors and results appear at the end.

2. **Merged stdout/stderr:** Combined into single stream. Simpler for the model to consume.

3. **Temp file for large output:** Full output preserved if model needs to inspect it.

4. **Process tree killing:** Commands often spawn children — kill them all.

5. **No stdin:** Commands can't read from stdin. This prevents hanging on interactive prompts.

6. **Detached process group:** Enables clean process tree management.

7. **Optional timeout:** No default timeout — long-running commands are allowed.

## Implementation Notes for Open Agent

### Minimal Implementation

```typescript
async function bash(command: string, cwd: string, timeout?: number): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn("bash", ["-c", command], {
      cwd,
      detached: true,
      stdio: ["ignore", "pipe", "pipe"],
    });
    
    const chunks: Buffer[] = [];
    
    child.stdout.on("data", (data) => chunks.push(data));
    child.stderr.on("data", (data) => chunks.push(data));
    
    let timeoutHandle: NodeJS.Timeout | undefined;
    if (timeout) {
      timeoutHandle = setTimeout(() => {
        process.kill(-child.pid!, "SIGKILL");
      }, timeout * 1000);
    }
    
    child.on("close", (code) => {
      if (timeoutHandle) clearTimeout(timeoutHandle);
      const output = Buffer.concat(chunks).toString("utf-8");
      
      if (code !== 0 && code !== null) {
        reject(new Error(`${output}\n\nCommand exited with code ${code}`));
      } else {
        resolve(output);
      }
    });
    
    child.on("error", reject);
  });
}
```

### Recommended Additions for Production

1. **Output truncation** — Implement tail truncation to avoid huge responses
2. **Temp file for large output** — Preserve full output for inspection
3. **Shell detection** — Cross-platform support (Windows Git Bash)
4. **Streaming updates** — Real-time output for long commands
5. **Binary output sanitization** — Strip control characters that break display

### Output Truncation Implementation

```typescript
const MAX_LINES = 2000;
const MAX_BYTES = 50 * 1024;

function truncateTail(content: string): { text: string; truncated: boolean } {
  const lines = content.split("\n");
  const totalBytes = Buffer.byteLength(content, "utf-8");
  
  if (lines.length <= MAX_LINES && totalBytes <= MAX_BYTES) {
    return { text: content, truncated: false };
  }
  
  // Take from end
  const result: string[] = [];
  let bytes = 0;
  
  for (let i = lines.length - 1; i >= 0 && result.length < MAX_LINES; i--) {
    const lineBytes = Buffer.byteLength(lines[i], "utf-8") + 1;
    if (bytes + lineBytes > MAX_BYTES) break;
    result.unshift(lines[i]);
    bytes += lineBytes;
  }
  
  return { text: result.join("\n"), truncated: true };
}
```
