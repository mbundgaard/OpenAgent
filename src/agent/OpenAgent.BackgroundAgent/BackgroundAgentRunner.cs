using System.Text;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks.SystemJobs;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// Orchestrates a single background agent run. Owns the three-gate check and the kickoff
/// turn that hands off to a text provider. Stays out of scheduling and state persistence —
/// <see cref="BackgroundAgentJob"/> wraps this for <c>SystemJobRunner</c> consumption.
///
/// The bg agent runs in a stable system conversation (<see cref="BackgroundConversationId"/>)
/// with <c>Source = "background"</c>, which makes <c>SystemPromptBuilder</c> include
/// BACKGROUND.md. The only outbound path is <c>post_to_main</c>; everything else stays
/// inside the bg conversation or the agent's sandbox folder.
/// </summary>
public sealed class BackgroundAgentRunner
{
    /// <summary>Stable ID for the bg agent's own conversation. Single instance, persistent.</summary>
    public const string BackgroundConversationId = "system:background-agent";

    /// <summary>Name under which <see cref="BackgroundAgentJob"/> registers in system-jobs.json.</summary>
    public const string JobName = "background-agent";

    private static readonly TimeSpan MinSinceLastRun = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinSinceLastMainMessage = TimeSpan.FromMinutes(15);
    private const int SandboxFilePreviewLines = 60;

    private readonly IConversationStore _store;
    private readonly Func<string, ILlmTextProvider> _textProviderResolver;
    private readonly AgentEnvironment _environment;
    private readonly AgentConfig _agentConfig;
    private readonly SystemJobStateStore _jobStateStore;
    private readonly ILogger<BackgroundAgentRunner> _logger;

    public BackgroundAgentRunner(
        IConversationStore store,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentEnvironment environment,
        AgentConfig agentConfig,
        SystemJobStateStore jobStateStore,
        ILogger<BackgroundAgentRunner> logger)
    {
        _store = store;
        _textProviderResolver = textProviderResolver;
        _environment = environment;
        _agentConfig = agentConfig;
        _jobStateStore = jobStateStore;
        _logger = logger;
    }

    /// <summary>
    /// Apply the three gates from BACKGROUND.md. Returns true only when all are satisfied.
    /// Time-of-day gate is enforced by the cron itself ("*/15 6-21 * * *"); this method
    /// covers the two interval gates plus a configuration sanity check.
    /// </summary>
    public Task<bool> ShouldRunAsync(DateTimeOffset now)
    {
        // Master switch — when the autonomous flow is disabled in agent.json the scheduled tick
        // skips silently. Per-conversation proactivity is handled by scheduled tasks instead.
        // Manual /api/background-agent/run still works; it doesn't consult this gate.
        if (!_agentConfig.BackgroundAgentEnabled)
        {
            _logger.LogDebug("Background agent gated: AgentConfig.BackgroundAgentEnabled is false");
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(_agentConfig.MainConversationId))
        {
            _logger.LogDebug("Background agent gated: AgentConfig.MainConversationId is not set");
            return Task.FromResult(false);
        }

        var mainConversation = _store.Get(_agentConfig.MainConversationId);
        if (mainConversation is null)
        {
            _logger.LogDebug("Background agent gated: main conversation '{Id}' not found",
                _agentConfig.MainConversationId);
            return Task.FromResult(false);
        }

        // Gate 1: minimum interval since our previous successful run (taken from the system-jobs
        // state file). A gated-out tick doesn't update LastRunAt, so this keeps re-evaluating
        // until enough time has passed.
        var jobState = _jobStateStore.GetOrCreate(JobName);
        if (jobState.LastRunAt is { } lastRun && now - lastRun < MinSinceLastRun)
        {
            _logger.LogDebug("Background agent gated: only {Elapsed} since last run (need {Required})",
                now - lastRun, MinSinceLastRun);
            return Task.FromResult(false);
        }

        // Gate 2: minimum quiet period in the main conversation. If the user is actively chatting,
        // don't barge in with [Background] posts.
        var messages = _store.GetMessages(_agentConfig.MainConversationId);
        var lastMessageAt = messages.Count == 0 ? (DateTimeOffset?)null : messages[^1].CreatedAt;
        if (lastMessageAt is { } lastMsg && now - lastMsg < MinSinceLastMainMessage)
        {
            _logger.LogDebug("Background agent gated: main conversation last active {Elapsed} ago (need {Required})",
                now - lastMsg, MinSinceLastMainMessage);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Run one autonomous turn: ensure the bg conversation exists, build the kickoff user message
    /// from INBOX.md + sandbox listing, and stream the text provider. The provider drives the
    /// tool loop; nothing the agent emits is delivered automatically — only explicit
    /// <c>post_to_main</c> calls reach the user.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_agentConfig.TextProvider) || string.IsNullOrWhiteSpace(_agentConfig.TextModel))
        {
            _logger.LogWarning("Background agent skipped: TextProvider or TextModel is unset");
            return;
        }

        var conversation = _store.GetOrCreate(
            BackgroundConversationId,
            "background",
            _agentConfig.TextProvider,
            _agentConfig.TextModel,
            _agentConfig.VoiceProvider,
            _agentConfig.VoiceModel);

        var prompt = BuildKickoffPrompt();
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = prompt
        };

        var provider = _textProviderResolver(conversation.TextProvider);
        var startedAt = DateTimeOffset.UtcNow;

        // Drain the completion. We don't care about the events — the agent's only outbound path
        // is post_to_main (explicit tool call). A "[]" final response from the provider already
        // triggers the suppression sentinel which discards the whole turn from history.
        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, ct))
        {
        }

        _logger.LogInformation("Background agent run complete in {Ms}ms",
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
    }

    /// <summary>
    /// Build the per-run kickoff user message: a short framing line plus the inbox contents and
    /// a listing of the sandbox folder. Both are loaded freshly each run so the agent sees the
    /// current state of its long-running notes.
    /// </summary>
    private string BuildKickoffPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Background run]");
        sb.AppendLine();

        sb.AppendLine("<inbox file=\"memory/background/INBOX.md\">");
        sb.AppendLine(ReadOrEmpty(Path.Combine(_environment.DataPath, "memory", "background", "INBOX.md"), "empty"));
        sb.AppendLine("</inbox>");
        sb.AppendLine();

        sb.AppendLine("<sandbox dir=\"memory/background/\">");
        sb.AppendLine(BuildSandboxListing());
        sb.AppendLine("</sandbox>");
        sb.AppendLine();

        sb.AppendLine("Process the inbox if anything is there. Otherwise, follow an open thread from " +
            "memory or the recent logs. Use post_to_main only when you have something genuinely worth " +
            "saying — otherwise end your turn with [] and the run stays silent. Update your sandbox " +
            "before finishing.");
        return sb.ToString();
    }

    private string BuildSandboxListing()
    {
        var sandboxDir = Path.Combine(_environment.DataPath, "memory", "background");
        if (!Directory.Exists(sandboxDir))
            return "empty (the directory will be created the first time you write a file there)";

        var files = Directory.GetFiles(sandboxDir, "*", SearchOption.AllDirectories)
            // Skip INBOX.md — it's already included separately above
            .Where(f => !string.Equals(Path.GetFileName(f), "INBOX.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            return "empty (no files besides INBOX.md)";

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(_environment.DataPath, file).Replace('\\', '/');
            sb.AppendLine($"--- {relative} ---");
            try
            {
                var lines = File.ReadLines(file).Take(SandboxFilePreviewLines).ToList();
                sb.AppendLine(string.Join('\n', lines));
                if (lines.Count == SandboxFilePreviewLines)
                    sb.AppendLine($"... [preview truncated at {SandboxFilePreviewLines} lines — read the file directly if you need more]");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[could not read: {ex.Message}]");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string ReadOrEmpty(string path, string emptyLabel)
    {
        if (!File.Exists(path)) return emptyLabel;
        var content = File.ReadAllText(path).Trim();
        return content.Length == 0 ? emptyLabel : content;
    }
}
