namespace OpenAgent;

/// <summary>
/// Resolves the agent's root directory. Reads the DATA_DIR environment variable first;
/// if unset, empty, or whitespace-only, falls back to the directory containing the running executable.
/// The env-var override behavior matches the existing Linux/Docker deployment; the fallback
/// is new and lets the Windows service run without any environment configuration.
/// </summary>
public static class RootResolver
{
    /// <summary>Resolves the root directory using the real environment.</summary>
    public static string Resolve() =>
        Resolve(
            envVar: Environment.GetEnvironmentVariable("DATA_DIR"),
            baseDirectory: AppContext.BaseDirectory);

    /// <summary>Test-friendly overload that takes both inputs explicitly.</summary>
    public static string Resolve(string? envVar, string baseDirectory) =>
        string.IsNullOrWhiteSpace(envVar) ? baseDirectory : envVar;
}
