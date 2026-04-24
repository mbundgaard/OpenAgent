namespace OpenAgent.Installer;

/// <summary>
/// Abstraction over Process.Start for install-mode shell-outs (sc.exe, netsh, cmd /c mklink).
/// Tests substitute a FakeSystemCommandRunner that records calls and returns canned results.
/// </summary>
public interface ISystemCommandRunner
{
    /// <summary>
    /// Runs the specified executable with arguments and returns the exit code and merged stdout/stderr.
    /// Blocks until the process exits or the timeout elapses.
    /// </summary>
    CommandResult Run(string executable, string arguments, TimeSpan? timeout = null);
}

public sealed record CommandResult(int ExitCode, string Output);
