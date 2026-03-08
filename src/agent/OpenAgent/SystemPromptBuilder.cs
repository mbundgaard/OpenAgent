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
    private readonly Dictionary<string, string> _files = new();

    // Prompt files and which conversation types include them
    private static readonly (string FilePath, ConversationType[] Types)[] FileMap =
    [
        ("AGENTS.md",        [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("SOUL.md",          [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("IDENTITY.md",      [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("USER.md",          [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("TOOLS.md",         [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("memory/MEMORY.md", [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("VOICE.md",         [ConversationType.Voice]),
    ];

    public SystemPromptBuilder(AgentEnvironment environment, ILogger<SystemPromptBuilder> logger)
    {
        _logger = logger;
        LoadFiles(environment.DataPath);
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
}
