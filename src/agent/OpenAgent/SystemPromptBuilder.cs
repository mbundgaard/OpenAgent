using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

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
    private readonly Dictionary<string, string> _files = new();

    // Prompt files and which conversation types include them
    private static readonly (string FilePath, ConversationType[] Types)[] FileMap =
    [
        ("AGENTS.md",        [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("SOUL.md",          [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("IDENTITY.md",      [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("USER.md",          [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("TOOLS.md",         [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("MEMORY.md", [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("VOICE.md",         [ConversationType.Voice]),
    ];

    public SystemPromptBuilder(AgentEnvironment environment, ILogger<SystemPromptBuilder> logger)
    {
        _logger = logger;
        _dataPath = environment.DataPath;
        LoadFiles(_dataPath);
    }

    /// <summary>Re-reads all prompt files from disk, replacing cached content.</summary>
    public void Reload()
    {
        _files.Clear();
        LoadFiles(_dataPath);
    }

    /// <summary>Returns the data path where prompt files are stored.</summary>
    public string DataPath => _dataPath;

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

            // After MEMORY.md, append yesterday's and today's daily memory
            if (filePath == "MEMORY.md")
            {
                var today = DateTime.UtcNow.Date;
                TryAppendFile(sections, Path.Combine("memory", $"{today.AddDays(-1):yyyy-MM-dd}.md"));
                TryAppendFile(sections, Path.Combine("memory", $"{today:yyyy-MM-dd}.md"));
            }
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
}
