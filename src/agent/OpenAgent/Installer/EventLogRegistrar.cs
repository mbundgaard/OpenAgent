using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenAgent.Installer;

/// <summary>
/// Ensures the "OpenAgent" Event Log source is registered in the Application log.
/// Called from --install (admin is required to write the registration).
/// Idempotent: no-op if the source already exists.
/// </summary>
public static class EventLogRegistrar
{
    public const string SourceName = "OpenAgent";
    public const string LogName = "Application";

    /// <summary>Creates the event source if it does not already exist. No-op on non-Windows.</summary>
    public static void Ensure()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        EnsureWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindows()
    {
        if (!EventLog.SourceExists(SourceName))
            EventLog.CreateEventSource(new EventSourceCreationData(SourceName, LogName));
    }
}
