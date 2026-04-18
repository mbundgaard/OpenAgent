using System.Reflection;
using System.Text.Json;
using OpenAgent.Installer;

namespace OpenAgent;

/// <summary>
/// Ensures the data directory has the required folder structure and default files.
/// Runs once at startup — creates missing folders, extracts embedded default files,
/// and creates configured directory links (junctions on Windows, symlinks on Linux).
/// Never overwrites existing files; reacts to invalid symlink configuration with warnings,
/// not failures.
/// </summary>
public static class DataDirectoryBootstrap
{
    private static readonly string[] RequiredDirectories =
    [
        "projects",
        "repos",
        "memory",
        "config",
        "connections",
        "skills"
    ];

    /// <summary>Production entry point. Uses <see cref="SystemCommandRunner"/> for link creation on Windows.</summary>
    public static void Run(string dataPath) => Run(dataPath, new DirectoryLinkCreator(new SystemCommandRunner()));

    /// <summary>Test-friendly overload that accepts an injected link creator.</summary>
    public static void Run(string dataPath, IDirectoryLinkCreator linkCreator)
    {
        // Ensure required directories exist
        foreach (var dir in RequiredDirectories)
            Directory.CreateDirectory(Path.Combine(dataPath, dir));

        // Extract embedded default markdown files
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "OpenAgent.defaults.";

        // If any personality file exists, this isn't a first run — don't re-create BOOTSTRAP.md
        var isFirstRun = !File.Exists(Path.Combine(dataPath, "AGENTS.md"));

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix))
                continue;

            var fileName = resourceName[prefix.Length..];
            var targetPath = Path.Combine(dataPath, fileName);

            if (File.Exists(targetPath))
                continue;

            if (fileName == "BOOTSTRAP.md" && !isFirstRun)
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            File.WriteAllText(targetPath, reader.ReadToEnd());
        }

        // Write default config files if missing
        var agentConfigPath = Path.Combine(dataPath, "config", "agent.json");
        if (!File.Exists(agentConfigPath))
            File.WriteAllText(agentConfigPath, "{\"symlinks\": {}}");

        var connectionsPath = Path.Combine(dataPath, "config", "connections.json");
        if (!File.Exists(connectionsPath))
            File.WriteAllText(connectionsPath, "[]");

        // Create configured directory links
        CreateConfiguredLinks(dataPath, agentConfigPath, linkCreator);
    }

    private static void CreateConfiguredLinks(string dataPath, string agentConfigPath, IDirectoryLinkCreator linkCreator)
    {
        Dictionary<string, string>? symlinks = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(agentConfigPath));
            if (doc.RootElement.TryGetProperty("symlinks", out var symlinksElement)
                && symlinksElement.ValueKind == JsonValueKind.Object)
            {
                symlinks = new Dictionary<string, string>();
                foreach (var prop in symlinksElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        symlinks[prop.Name] = prop.Value.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"[bootstrap] Failed to parse {agentConfigPath}; skipping symlink creation.");
            return;
        }

        if (symlinks is null)
            return;

        foreach (var (name, target) in symlinks)
        {
            var linkPath = Path.Combine(dataPath, name);

            if (linkCreator.LinkExists(linkPath))
            {
                var existingTarget = linkCreator.ReadLinkTarget(linkPath);
                if (existingTarget is not null && PathsEqual(existingTarget, target))
                    continue;

                Console.Error.WriteLine(
                    $"[bootstrap] Link {linkPath} already exists and points to {existingTarget ?? "<unknown>"}; " +
                    $"configured target {target} ignored.");
                continue;
            }

            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                Console.Error.WriteLine(
                    $"[bootstrap] {linkPath} exists as a regular directory/file; skipping junction creation.");
                continue;
            }

            if (!Directory.Exists(target))
            {
                Console.Error.WriteLine(
                    $"[bootstrap] Symlink target {target} does not exist; skipping junction {linkPath}.");
                continue;
            }

            try
            {
                linkCreator.CreateLink(linkPath, target);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bootstrap] Failed to create junction {linkPath} -> {target}: {ex.Message}");
            }
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
