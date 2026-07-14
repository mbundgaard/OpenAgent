using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// Runs once at host startup to delete orphaned heartbeat nudges - "[Heartbeat]" user messages
/// left behind in the main conversation by a heartbeat that never reached
/// <see cref="BackgroundAgentRunner.RunAsync"/>'s <c>finally</c> cleanup because the process was
/// killed mid-turn (SIGKILL, container OOM-kill, power loss). Azure restarts containers
/// routinely, so this is not a hypothetical: an orphaned nudge sits in the main conversation
/// forever otherwise, visible in the web/app conversation view and re-injected into every
/// subsequent LLM context.
///
/// Deliberately independent of <see cref="SystemJobRunner"/>'s cron/gate machinery - the sweep
/// must run immediately on startup, not wait for the heartbeat's own 15/30-minute gates or its
/// "*/15 6-21" cron window, which could delay cleanup for hours.
/// </summary>
public sealed class BackgroundAgentNudgeSweep : IHostedService
{
    private readonly IConversationStore _store;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<BackgroundAgentNudgeSweep> _logger;

    public BackgroundAgentNudgeSweep(
        IConversationStore store,
        AgentConfig agentConfig,
        ILogger<BackgroundAgentNudgeSweep> logger)
    {
        _store = store;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SweepOrphanedNudges();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Deletes any leftover heartbeat nudges from the main conversation. Public (rather than
    /// folded directly into <see cref="StartAsync"/>) so tests can invoke the sweep logic
    /// directly without spinning up hosting infrastructure.
    /// </summary>
    public void SweepOrphanedNudges()
    {
        var mainConversationId = _agentConfig.MainConversationId;
        if (string.IsNullOrWhiteSpace(mainConversationId))
            return;

        if (_store.Get(mainConversationId) is null)
            return;

        var orphanedNudgeIds = _store.GetMessages(mainConversationId)
            .Where(m => m.Role == "user"
                        && m.Content is not null
                        && m.Content.StartsWith(BackgroundAgentRunner.NudgeMarker, StringComparison.Ordinal))
            .Select(m => m.Id)
            .ToList();

        if (orphanedNudgeIds.Count == 0)
            return;

        _store.DeleteMessages(mainConversationId, orphanedNudgeIds);
        _logger.LogWarning(
            "Background agent startup sweep removed {Count} orphaned heartbeat nudge(s) from conversation '{ConversationId}' - likely left behind by a hard kill mid-turn",
            orphanedNudgeIds.Count, mainConversationId);
    }
}
