using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent;

/// <summary>
/// Composes the system prompt from markdown files in the data directory.
/// Static files are cached at startup. Daily memory files are read fresh.
/// </summary>
internal sealed class SystemPromptBuilder
{
    private readonly ILogger<SystemPromptBuilder> _logger;
    private readonly string _dataPath;
    private readonly Dictionary<string, string> _files = new();

    private static readonly ConversationType[] AllTypes =
        [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook];

    // Prompt files and which conversation types include them
    private static readonly (string FilePath, ConversationType[] Types)[] FileMap =
    [
        ("AGENTS.md",        AllTypes),
        ("SOUL.md",          AllTypes),
        ("IDENTITY.md",      AllTypes),
        ("USER.md",          AllTypes),
        ("TOOLS.md",         AllTypes),
        ("memory/MEMORY.md", AllTypes),
        ("VOICE.md",         [ConversationType.Voice]),
    ];

    public SystemPromptBuilder(AgentEnvironment environment, ILogger<SystemPromptBuilder> logger)
    {
        _logger = logger;
        _dataPath = environment.DataPath;
        LoadFiles(_dataPath);
    }

    /// <summary>
    /// Builds the system prompt for the given conversation type by concatenating
    /// the relevant files in order, separated by blank lines.
    /// </summary>
    public string Build(ConversationType type)
    {
        var sections = new List<string>();

        foreach (var (filePath, types) in FileMap)
        {
            if (!types.Contains(type))
                continue;

            if (_files.TryGetValue(filePath, out var content))
                sections.Add(content);
        }

        // Daily memory — yesterday and today, read fresh
        var today = DateTime.UtcNow.Date;
        TryAppendFile(sections, Path.Combine(_dataPath, "memory", $"{today.AddDays(-1):yyyy-MM-dd}.md"));
        TryAppendFile(sections, Path.Combine(_dataPath, "memory", $"{today:yyyy-MM-dd}.md"));

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

    private static void TryAppendFile(List<string> sections, string path)
    {
        if (!File.Exists(path)) return;
        var content = File.ReadAllText(path).Trim();
        if (content.Length > 0) sections.Add(content);
    }
}
