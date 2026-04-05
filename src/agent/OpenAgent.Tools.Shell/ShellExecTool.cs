using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Shell;

/// <summary>
/// Executes a shell command in the workspace and returns output.
/// Supports timeout, tail truncation, process tree killing, and merged stdout/stderr.
/// </summary>
public sealed class ShellExecTool(string workspacePath, ILogger<ShellExecTool> logger, int defaultTimeoutSec = 30, int maxLines = 2000, int maxBytes = 50 * 1024) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "shell_exec",
        Description = "Execute a shell command in the workspace. Output is tail-truncated to the last 2000 lines or 50KB. Errors and results appear at the end of output.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "Shell command to execute" },
                cwd = new { type = "string", description = "Working directory relative to workspace (optional)" },
                timeout = new { type = "integer", description = "Timeout in seconds (default 30, kills process on expiry)" }
            },
            required = new[] { "command" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var command = args.GetProperty("command").GetString()
            ?? throw new ArgumentException("command is required");

        logger.LogDebug("shell_exec: command={Command}", command);

        // Parse optional parameters
        var timeoutSec = args.TryGetProperty("timeout", out var timeoutEl) && timeoutEl.ValueKind == JsonValueKind.Number
            ? Math.Max(1, timeoutEl.GetInt32())
            : defaultTimeoutSec;

        // Resolve working directory
        var workingDir = workspacePath;
        if (args.TryGetProperty("cwd", out var cwdEl) && cwdEl.GetString() is { } cwd)
        {
            var resolvedCwd = Path.GetFullPath(Path.Combine(workspacePath, cwd));
            if (!resolvedCwd.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { error = "working directory escapes workspace" });
            if (!Directory.Exists(resolvedCwd))
                return JsonSerializer.Serialize(new { error = $"working directory does not exist: {cwd}" });
            workingDir = resolvedCwd;
        }

        // Determine shell — bash on Linux/macOS, bash (Git Bash) or cmd on Windows
        var (shell, shellArgs) = GetShellConfig();
        logger.LogDebug("shell_exec: shell={Shell}, args=[{ShellArgs}], cwd={WorkingDir}",
            shell, string.Join(", ", shellArgs), workingDir);

        // Start the process
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in shellArgs)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };

        // Collect merged stdout/stderr in arrival order
        var outputChunks = new List<string>();
        var outputLock = new object();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (outputLock) outputChunks.Add(e.Data);
                logger.LogDebug("shell_exec [stdout]: {Line}", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (outputLock) outputChunks.Add(e.Data);
                logger.LogDebug("shell_exec [stderr]: {Line}", e.Data);
            }
        };

        process.Start();
        logger.LogDebug("shell_exec: process started, pid={Pid}", process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait with timeout
        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            timedOut = true;
            logger.LogDebug("shell_exec: timed out after {TimeoutSec}s, killing process tree", timeoutSec);
            KillProcessTree(process);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled
            logger.LogDebug("shell_exec: cancelled by caller, killing process tree");
            KillProcessTree(process);
            throw;
        }

        // Build output
        string[] lines;
        lock (outputLock) lines = outputChunks.ToArray();

        logger.LogDebug("shell_exec: captured {LineCount} lines, exit_code={ExitCode}, timed_out={TimedOut}",
            lines.Length, timedOut ? -1 : process.ExitCode, timedOut);

        var output = TruncateTail(lines);

        // Append status line
        if (timedOut)
            output += $"\n\nCommand timed out after {timeoutSec} seconds";
        else
            output += $"\n\nCommand exited with code {process.ExitCode}";

        logger.LogDebug("shell_exec: final output length={OutputLength} chars", output.Length);

        return JsonSerializer.Serialize(new { output });
    }

    /// <summary>
    /// Keeps the tail of the output (where errors and results appear).
    /// Truncates from the beginning if output exceeds line or byte limits.
    /// </summary>
    private string TruncateTail(string[] lines)
    {
        var totalLines = lines.Length;

        if (totalLines <= maxLines)
        {
            var joined = string.Join("\n", lines);
            if (Encoding.UTF8.GetByteCount(joined) <= maxBytes)
                return joined;
        }

        // Work backwards from the end, collecting lines within limits
        var selected = new List<string>();
        var byteCount = 0;

        for (var i = lines.Length - 1; i >= 0 && selected.Count < maxLines; i--)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + 1; // +1 for newline
            if (byteCount + lineBytes > maxBytes && selected.Count > 0)
                break;

            selected.Add(lines[i]);
            byteCount += lineBytes;
        }

        selected.Reverse();

        var header = $"[showing last {selected.Count} of {totalLines} lines]\n\n";
        return header + string.Join("\n", selected);
    }

    /// <summary>
    /// Kills the process and all its children.
    /// </summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            // .NET 9+ Process.Kill(entireProcessTree: true) handles this cross-platform
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have already exited
        }
    }

    private static (string Shell, string[] Args) GetShellConfig()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (File.Exists("/bin/bash"))
                return ("/bin/bash", ["-c"]);
            return ("sh", ["-c"]);
        }

        // Windows: try Git Bash, fall back to cmd
        var gitBashPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
        };

        foreach (var path in gitBashPaths)
        {
            if (File.Exists(path))
                return (path, ["-c"]);
        }

        return ("cmd.exe", ["/C"]);
    }
}
