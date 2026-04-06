using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Skills;

/// <summary>
/// Holds discovered skills and provides the catalog prompt for the system message.
/// Loaded at startup, supports reload for picking up new/changed skills.
/// </summary>
public sealed class SkillCatalog
{
    private readonly string _skillsRoot;
    private readonly ILogger? _logger;
    private Dictionary<string, SkillEntry> _skills = new(StringComparer.OrdinalIgnoreCase);

    public SkillCatalog(string skillsRoot, ILogger<SkillCatalog>? logger = null)
    {
        _skillsRoot = skillsRoot;
        _logger = logger;
        LoadSkills();
    }

    /// <summary>All discovered skill names.</summary>
    public IReadOnlyList<string> SkillNames => _skills.Keys.ToList();

    /// <summary>All discovered skill entries.</summary>
    public IReadOnlyList<SkillEntry> Skills => _skills.Values.ToList();

    /// <summary>
    /// Looks up a skill by name. Returns false if not found.
    /// </summary>
    public bool TryGetSkill(string name, out SkillEntry? skill)
    {
        return _skills.TryGetValue(name, out skill);
    }

    /// <summary>
    /// Builds the XML catalog prompt for injection into the system message.
    /// Returns empty string if no skills are available (per spec: omit catalog entirely).
    /// </summary>
    public string BuildCatalogPrompt()
    {
        if (_skills.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("<available_skills>");

        foreach (var skill in _skills.Values)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{EscapeXml(skill.Name)}</name>");
            sb.AppendLine($"    <description>{EscapeXml(skill.Description)}</description>");
            sb.AppendLine($"    <location>{EscapeXml(skill.Location)}</location>");
            sb.AppendLine("  </skill>");
        }

        sb.Append("</available_skills>");
        return sb.ToString();
    }

    /// <summary>
    /// Reads the skill body fresh from disk. Returns null if the file is missing or invalid.
    /// Used by SystemPromptBuilder to pick up SKILL.md changes without restart.
    /// </summary>
    public string? ReadSkillBody(string skillName)
    {
        if (!_skills.TryGetValue(skillName, out var skill))
            return null;

        try
        {
            var content = File.ReadAllText(skill.Location);
            var parsed = SkillFrontmatterParser.Parse(content);
            return parsed.IsValid ? parsed.Body : null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to re-read skill {Name} from disk", skillName);
            return null;
        }
    }

    /// <summary>
    /// Lists files in scripts/, references/, and assets/ subdirectories for a skill.
    /// Returns paths relative to the skill directory using forward slashes.
    /// </summary>
    public IReadOnlyList<string> GetSkillResources(string skillName, int maxResources = 50)
    {
        if (!_skills.TryGetValue(skillName, out var skill))
            return [];

        var skillDir = Path.GetDirectoryName(skill.Location)!;
        var resources = new List<string>();
        var resourceDirs = new[] { "scripts", "references", "assets" };

        foreach (var subDir in resourceDirs)
        {
            var fullSubDir = Path.Combine(skillDir, subDir);
            if (!Directory.Exists(fullSubDir))
                continue;

            foreach (var file in Directory.GetFiles(fullSubDir, "*", SearchOption.AllDirectories))
            {
                if (resources.Count >= maxResources)
                    break;
                resources.Add(Path.GetRelativePath(skillDir, file).Replace('\\', '/'));
            }
        }

        return resources;
    }

    /// <summary>Re-scans the skills directory and replaces the in-memory catalog.</summary>
    public void Reload()
    {
        LoadSkills();
    }

    private void LoadSkills()
    {
        var entries = SkillDiscovery.Scan(_skillsRoot, logger: _logger);
        _skills = entries.ToDictionary(e => e.Name, e => e, StringComparer.OrdinalIgnoreCase);
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
