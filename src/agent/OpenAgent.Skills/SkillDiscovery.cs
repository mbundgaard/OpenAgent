using Microsoft.Extensions.Logging;

namespace OpenAgent.Skills;

/// <summary>
/// Scans a directory for skill subdirectories containing SKILL.md files.
/// Each valid skill directory must contain a SKILL.md with valid frontmatter.
/// Follows the Agent Skills spec: one level deep, skip hidden/ignored dirs.
/// </summary>
public static class SkillDiscovery
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "dist", ".venv", "__pycache__", ".cache", "build", "bin", "obj"
    };

    /// <summary>Max SKILL.md file size in bytes (256 KB per spec).</summary>
    private const int MaxSkillFileBytes = 256_000;

    /// <summary>
    /// Scans the given directory for subdirectories containing a SKILL.md file.
    /// Returns a list of successfully parsed SkillEntry objects.
    /// Silently skips invalid skills (logged at Debug level if logger provided).
    /// </summary>
    public static IReadOnlyList<SkillEntry> Scan(string skillsRoot, int maxSkills = 200, ILogger? logger = null)
    {
        if (!Directory.Exists(skillsRoot))
        {
            logger?.LogDebug("Skills directory does not exist: {Path}", skillsRoot);
            return [];
        }

        var results = new List<SkillEntry>();

        // Scan one level deep: each subdirectory is a potential skill
        var directories = Directory.GetDirectories(skillsRoot);

        foreach (var dir in directories)
        {
            if (results.Count >= maxSkills)
                break;

            var dirName = Path.GetFileName(dir);

            // Skip hidden and ignored directories
            if (dirName.StartsWith('.') || IgnoredDirectories.Contains(dirName))
                continue;

            var skillMdPath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillMdPath))
                continue;

            // Guard against oversized files
            var fileInfo = new FileInfo(skillMdPath);
            if (fileInfo.Length > MaxSkillFileBytes)
            {
                logger?.LogWarning("Skipping oversized SKILL.md ({Bytes} bytes): {Path}", fileInfo.Length, skillMdPath);
                continue;
            }

            var content = File.ReadAllText(skillMdPath);
            var parsed = SkillFrontmatterParser.Parse(content);

            if (!parsed.IsValid)
            {
                logger?.LogDebug("Skipping invalid SKILL.md in {Dir}: {Error}", dirName, parsed.Error);
                continue;
            }

            results.Add(new SkillEntry
            {
                Name = parsed.Name!,
                Description = parsed.Description!,
                Location = Path.GetFullPath(skillMdPath),
                Body = parsed.Body!,
                License = parsed.License,
                Compatibility = parsed.Compatibility,
                Metadata = parsed.Metadata
            });
        }

        logger?.LogInformation("Discovered {Count} skills in {Root}", results.Count, skillsRoot);
        return results;
    }
}
