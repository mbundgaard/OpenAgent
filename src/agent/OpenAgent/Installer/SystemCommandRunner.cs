using System.Diagnostics;

namespace OpenAgent.Installer;

public sealed class SystemCommandRunner : ISystemCommandRunner
{
    public CommandResult Run(string executable, string arguments, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {executable}");

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{executable} did not exit within {effectiveTimeout.TotalSeconds}s");
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        return new CommandResult(process.ExitCode, output);
    }
}
