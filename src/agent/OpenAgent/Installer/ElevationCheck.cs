using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenAgent.Installer;

/// <summary>
/// Elevation/admin checks for install-mode commands. On non-Windows platforms these are
/// no-ops (IsAdministrator returns true) — install-mode commands are Windows-only and
/// their main entry points already gate on OS.
/// </summary>
public static class ElevationCheck
{
    /// <summary>True if the current process runs as a member of the local Administrators group.</summary>
    public static bool IsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true;

#pragma warning disable CA1416 // validated by IsOSPlatform above
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
    }

    /// <summary>
    /// Returns 0 if elevated. Otherwise writes an explanation to stderr and returns a non-zero exit code
    /// for the caller to pass back from Main.
    /// </summary>
    public static int RequireAdministrator(string verb)
    {
        if (IsAdministrator())
            return 0;

        Console.Error.WriteLine($"{verb} requires administrator privileges. Run this command from an elevated (\"Run as Administrator\") prompt.");
        return 5; // ERROR_ACCESS_DENIED
    }
}
