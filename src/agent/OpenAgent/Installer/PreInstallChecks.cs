namespace OpenAgent.Installer;

/// <summary>
/// Validates that the install source folder and the host environment are ready
/// to register the service. Each check returns a CheckResult for the caller
/// to aggregate and surface as a single clear error.
/// </summary>
public static class PreInstallChecks
{
    public static CheckResult VerifyBridgeScriptPresent(string sourceFolder)
    {
        var bridgePath = Path.Combine(sourceFolder, "node", "baileys-bridge.js");
        return File.Exists(bridgePath)
            ? CheckResult.Success
            : new CheckResult(false, $"Expected node\\baileys-bridge.js next to the exe (looked at {bridgePath}). Extract the full distribution before running --install.");
    }

    public static CheckResult VerifyNodeAvailable(ISystemCommandRunner runner)
    {
        CommandResult result;
        try
        {
            result = runner.Run("node", "--version", timeout: TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"Failed to invoke 'node --version': {ex.Message}. Install Node.js 22+ from https://nodejs.org and ensure it is on PATH.");
        }

        return result.ExitCode == 0
            ? CheckResult.Success
            : new CheckResult(false, $"'node --version' exited {result.ExitCode}. Install Node.js 22+ from https://nodejs.org and ensure it is on PATH.");
    }

    public static CheckResult VerifyPathSafe(string path)
    {
        foreach (var c in path)
        {
            if (c == '\0' || c == '\n' || c == '\r')
                return new CheckResult(false, $"Install path contains unsupported characters: {path}");
        }
        return CheckResult.Success;
    }
}

public sealed record CheckResult(bool Ok, string Message)
{
    public static CheckResult Success { get; } = new(true, "");
}
