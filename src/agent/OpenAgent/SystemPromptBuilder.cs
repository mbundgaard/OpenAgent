using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Skills;
using System.Linq;

namespace OpenAgent;

/// <summary>
/// Composes the system prompt from markdown files in the data directory.
/// Files are loaded once at startup and cached. Which files are included
/// depends on the conversation type.
/// </summary>
internal sealed class SystemPromptBuilder
{
    private readonly ILogger<SystemPromptBuilder> _logger;
    private readonly string _dataPath;
    private readonly SkillCatalog _skillCatalog;
    private readonly AgentConfig _agentConfig;
    private readonly Dictionary<string, string> _files = new();

    // Prompt files and which conversation types include them
    private static readonly (string FilePath, ConversationType[] Types)[] FileMap =
    [
        ("AGENTS.md",        [ConversationType.Text, ConversationType.Voice]),
        ("SOUL.md",          [ConversationType.Text, ConversationType.Voice]),
        ("IDENTITY.md",      [ConversationType.Text, ConversationType.Voice]),
        ("USER.md",          [ConversationType.Text, ConversationType.Voice]),
        ("TOOLS.md",         [ConversationType.Text, ConversationType.Voice]),
        ("MEMORY.md",        [ConversationType.Text, ConversationType.Voice]),
        ("VOICE.md",         [ConversationType.Voice]),
    ];

    public SystemPromptBuilder(AgentEnvironment environment, SkillCatalog skillCatalog, AgentConfig agentConfig, ILogger<SystemPromptBuilder> logger)
    {
        _logger = logger;
        _dataPath = environment.DataPath;
        _skillCatalog = skillCatalog;
        _agentConfig = agentConfig;
        LoadFiles(_dataPath);
    }

    /// <summary>Re-reads all prompt files from disk, replacing cached content.</summary>
    public void Reload()
    {
        _files.Clear();
        LoadFiles(_dataPath);
        _skillCatalog.Reload();
    }

    /// <summary>Returns the data path where prompt files are stored.</summary>
    public string DataPath => _dataPath;

    /// <summary>
    /// Builds the system prompt for the given conversation type by concatenating
    /// the relevant files in order, separated by blank lines.
    /// </summary>
    public string Build(ConversationType type, IReadOnlyList<string>? activeSkills = null, string? intention = null)
    {
        var sections = new List<string>();

        foreach (var (filePath, types) in FileMap)
        {
            if (!types.Contains(type))
                continue;

            if (_files.TryGetValue(filePath, out var content))
                sections.Add(content);

            // After MEMORY.md, append every daily file still in memory/ root (newest first).
            // The indexer moves indexed files to memory/backup/, which is not scanned here, so
            // the prompt naturally contains exactly the "live" set that hasn't rolled past the
            // memoryDays window and been absorbed into memory_chunks. MemoryDays is the
            // indexer's threshold only — this loader just reflects whatever the indexer has
            // left on disk.
            if (filePath == "MEMORY.md")
            {
                var memoryDir = Path.Combine(_dataPath, "memory");
                if (Directory.Exists(memoryDir))
                {
                    var liveFiles = Directory.GetFiles(memoryDir, "????-??-??.md")
                        .OrderByDescending(f => Path.GetFileName(f));
                    foreach (var file in liveFiles)
                        TryAppendFile(sections, Path.Combine("memory", Path.GetFileName(file)));
                }
            }
        }

        // Append skill catalog when skills are available
        var catalogPrompt = _skillCatalog.BuildCatalogPrompt();
        if (catalogPrompt.Length > 0)
        {
            var skillSection = """
                The following skills provide specialized instructions for specific tasks.
                When a task matches a skill's description, call the activate_skill tool
                with the skill's name to load its full instructions. Use deactivate_skill
                to remove a skill when you no longer need it. Use list_active_skills to
                see which skills are currently active. Use activate_skill_resource to load
                supporting files (scripts, references) from an active skill.
                Paths in skill instructions are relative to the skill directory. Each
                active_skill tag includes a directory attribute — prefix relative paths
                with it when using shell_exec (e.g. "{directory}/scripts/build.sh").

                """ + catalogPrompt;
            sections.Add(skillSection);
        }

        // Append active skill bodies — these are permanent in the system prompt
        if (activeSkills is { Count: > 0 })
        {
            foreach (var skillName in activeSkills)
            {
                if (!_skillCatalog.TryGetSkill(skillName, out var skill))
                    continue;

                // Read skill body fresh from disk each time — picks up changes without restart
                var body = _skillCatalog.ReadSkillBody(skillName) ?? skill!.Body;

                var resources = _skillCatalog.GetSkillResources(skillName);
                var skillDir = Path.GetDirectoryName(skill!.Location)!;
                var relativeSkillDir = Path.GetRelativePath(_dataPath, skillDir).Replace('\\', '/');
                var skillSection = $"<active_skill name=\"{skill.Name}\" directory=\"{relativeSkillDir}\">\n{body}";

                if (resources.Count > 0)
                {
                    skillSection += "\n\n<skill_resources>";
                    foreach (var resource in resources)
                        skillSection += $"\n  <file>{resource}</file>";
                    skillSection += "\n</skill_resources>";
                }

                skillSection += "\n</active_skill>";
                sections.Add(skillSection);
            }
        }

        // Inject mount-point conventions when symlink roots are present — helps the agent
        // translate real paths from shell output to the short form expected by file tools.
        var symlinkRoots = EnumerateTopLevelReparsePoints(_dataPath);
        if (symlinkRoots.Count > 0)
        {
            var lines = symlinkRoots.Select(name =>
                $"  - {name}/... — reaches a mounted external path; shell output may show real paths under this mount, translate to the short form when passing to file tools.");
            sections.Add(
                "<path_conventions>\n" +
                "When passing paths to file tools, use paths relative to the agent's root. Configured mount points:\n" +
                string.Join("\n", lines) + "\n" +
                "</path_conventions>");
        }

        // Inject current datetime in the agent's configured timezone (Europe/Copenhagen)
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var utcLabel = now.Offset == TimeSpan.Zero ? "UTC" : $"UTC{now.Offset.Hours:+0;-0}";
        var weekNumber = System.Globalization.ISOWeek.GetWeekOfYear(now.DateTime);
        var weekday = now.DateTime.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture);
        sections.Add($"Current time: {weekday} {now:yyyy-MM-ddTHH:mm} Europe/Copenhagen ({utcLabel}), week {weekNumber}");

        // Per-conversation intention — scopes the topic and anchors the agent across turns.
        // Placed last so it's the most recent context the model sees before the user turns.
        if (!string.IsNullOrWhiteSpace(intention))
        {
            sections.Add($"""
                <conversation_intention>
                This conversation is scoped to the following topic/purpose. Keep replies on-topic
                and redirect off-topic turns back to it. If the user explicitly changes the topic,
                acknowledge the shift but do not invent tangents on your own.

                {intention.Trim()}
                </conversation_intention>
                """);
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>Reads all known prompt files from disk. Missing files are skipped.</summary>
    private void LoadFiles(string dataPath)
    {
        foreach (var (filePath, _) in FileMap)
        {
            var fullPath = Path.Combine(dataPath, filePath);
            if (!File.Exists(fullPath))
            {
                _logger.LogDebug("Prompt file not found, skipping: {Path}", fullPath);
                continue;
            }

            _files[filePath] = File.ReadAllText(fullPath).Trim();
            _logger.LogInformation("Loaded prompt file: {FilePath}", filePath);
        }
    }

    private void TryAppendFile(List<string> sections, string relativePath)
    {
        var fullPath = Path.Combine(_dataPath, relativePath);
        if (!File.Exists(fullPath)) return;
        var content = File.ReadAllText(fullPath).Trim();
        if (content.Length > 0) sections.Add(content);
    }

    private static List<string> EnumerateTopLevelReparsePoints(string dataPath)
    {
        var names = new List<string>();
        if (!Directory.Exists(dataPath))
            return names;

        foreach (var dir in Directory.EnumerateDirectories(dataPath, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                names.Add(info.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
