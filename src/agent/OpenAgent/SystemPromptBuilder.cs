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

    // Files and which conversation types include them
    private static readonly (string FileName, ConversationType[] Types)[] FileMap =
    [
        ("AGENTS.md",   [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("SOUL.md",     [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("IDENTITY.md", [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("USER.md",     [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("TOOLS.md",    [ConversationType.Text, ConversationType.Voice, ConversationType.Cron, ConversationType.WebHook]),
        ("VOICE.md",    [ConversationType.Voice]),
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

        foreach (var (fileName, types) in FileMap)
        {
            if (!types.Contains(type))
                continue;

            if (_files.TryGetValue(fileName, out var content))
                sections.Add(content);
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>Reads all known prompt files from disk. Missing files are skipped.</summary>
    private void LoadFiles(string dataPath)
    {
        foreach (var (fileName, _) in FileMap)
        {
            var path = Path.Combine(dataPath, fileName);
            if (!File.Exists(path))
            {
                _logger.LogDebug("Prompt file not found, skipping: {Path}", path);
                continue;
            }

            _files[fileName] = File.ReadAllText(path).Trim();
            _logger.LogInformation("Loaded prompt file: {FileName}", fileName);
        }
    }
}
