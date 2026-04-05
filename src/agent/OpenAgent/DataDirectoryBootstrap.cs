using System.Reflection;

namespace OpenAgent;

/// <summary>
/// Ensures the data directory has the required folder structure and default files.
/// Runs once at startup — creates missing folders and extracts embedded default files
/// that don't exist yet. Never overwrites existing files.
/// </summary>
public static class DataDirectoryBootstrap
{
    private static readonly string[] RequiredDirectories =
    [
        "projects",
        "repos",
        "memory",
        "config",
        "connections"
    ];

    /// <summary>
    /// Creates missing directories, extracts embedded default markdown files,
    /// and writes default config JSON files if they don't exist.
    /// </summary>
    public static void Run(string dataPath)
    {
        // Ensure required directories exist
        foreach (var dir in RequiredDirectories)
            Directory.CreateDirectory(Path.Combine(dataPath, dir));

        // Extract embedded default markdown files
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "OpenAgent.defaults.";

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix))
                continue;

            // Resource name: OpenAgent.defaults.AGENTS.md -> AGENTS.md
            var fileName = resourceName[prefix.Length..];
            var targetPath = Path.Combine(dataPath, fileName);

            if (File.Exists(targetPath))
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            File.WriteAllText(targetPath, reader.ReadToEnd());
        }

        // Write default config files if missing
        var agentConfigPath = Path.Combine(dataPath, "config", "agent.json");
        if (!File.Exists(agentConfigPath))
            File.WriteAllText(agentConfigPath, "{}");

        var connectionsPath = Path.Combine(dataPath, "config", "connections.json");
        if (!File.Exists(connectionsPath))
            File.WriteAllText(connectionsPath, "[]");
    }
}
