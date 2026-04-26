using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Tools.Shell;

namespace OpenAgent.Tests;

/// <summary>
/// Pinning tests for <see cref="ShellExecTool"/> cancellation behavior. The Telnyx phone bridge
/// cancels its CTS during teardown — a shell command that's still spinning must die promptly,
/// otherwise the call drags on while the process tree keeps running. ShellExecTool re-throws
/// caller cancellation after killing the process tree (distinct from timeout, which returns a
/// regular result).
/// </summary>
public class ShellExecToolCancellationTests
{
    [Fact]
    public async Task ExecuteAsync_HonoursCancellationToken_KillsProcessAndThrows()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"openagent-shell-cancel-{Guid.NewGuid()}");
        Directory.CreateDirectory(workspace);

        try
        {
            var tool = new ShellExecTool(workspace, NullLogger<ShellExecTool>.Instance, defaultTimeoutSec: 30);

            // Pick a long-running command that works on whichever shell the tool selects.
            // Git Bash + Linux: `sleep 30`; cmd.exe fallback: a 30-iteration ping localhost.
            var command = ResolveLongRunningCommand();
            var argsJson = $$"""{"command": {{System.Text.Json.JsonSerializer.Serialize(command)}}, "timeout": 30}""";

            using var cts = new CancellationTokenSource();

            var sw = Stopwatch.StartNew();
            var task = tool.ExecuteAsync(argsJson, "conv-1", cts.Token);

            // Let the process spawn before we cancel so we exercise mid-flight cancellation,
            // not pre-start cancellation.
            await Task.Delay(150);
            cts.Cancel();

            // Tool re-throws caller cancellation (after killing the process tree). The wait
            // budget is generous enough to absorb process-spawn + tree-kill on slow CI.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            });

            sw.Stop();

            // The whole flow — spawn, cancel, kill, throw — must complete well inside the
            // 30s timeout budget; otherwise the bridge teardown would drag.
            Assert.True(sw.ElapsedMilliseconds < 5000,
                $"shell_exec did not honour cancellation in time (took {sw.ElapsedMilliseconds}ms)");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Builds a command that blocks for ~30 seconds on whichever shell <see cref="ShellExecTool"/>
    /// will pick. Git Bash + Linux use <c>sleep</c>; the cmd.exe fallback uses <c>ping</c>.
    /// </summary>
    private static string ResolveLongRunningCommand()
    {
        if (!OperatingSystem.IsWindows())
            return "sleep 30";

        // Match the same probe ShellExecTool.GetShellConfig uses internally.
        var gitBashPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
        };
        var hasGitBash = gitBashPaths.Any(File.Exists);

        return hasGitBash
            ? "sleep 30"
            : "ping -n 30 127.0.0.1 >NUL";
    }
}
