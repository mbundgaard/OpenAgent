using System.Text;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks;
using OpenAgent.ScheduledTasks.SystemJobs;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// Orchestrates a single heartbeat. The heartbeat is a nudge, not a separate agent: it injects an
/// ephemeral user message into the user's main conversation and lets the agent take an ordinary
/// turn there - full history, all tools, its real system prompt.
///
/// Because the turn happens in the main conversation, the agent can see what it already said and
/// what the user answered. That is the whole design: perception and memory-of-speech are not
/// features, they are consequences of living in the thread. The previous architecture ran in an
/// isolated conversation with no way to read main, and re-asked the same questions six times in a
/// day - twice after the user had already answered them.
///
/// The nudge itself is never persisted. The agent either replies (the provider persists it, and we
/// deliver it to the bound channel) or emits the "[]" sentinel (the provider discards the whole
/// turn, and the thread is untouched).
/// </summary>
public sealed class BackgroundAgentRunner
{
    /// <summary>Name under which <see cref="BackgroundAgentJob"/> registers in system-jobs.json.</summary>
    public const string JobName = "background-agent";

    /// <summary>Literal prefix every heartbeat nudge begins with.</summary>
    public const string NudgeMarker = "[Heartbeat]";

    private static readonly TimeSpan MinSinceLastRun = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinSinceLastMainMessage = TimeSpan.FromMinutes(15);

    private readonly IConversationStore _store;
    private readonly Func<string, ILlmTextProvider> _textProviderResolver;
    private readonly AgentEnvironment _environment;
    private readonly AgentConfig _agentConfig;
    private readonly SystemJobStateStore _jobStateStore;
    private readonly DeliveryRouter _deliveryRouter;
    private readonly ILogger<BackgroundAgentRunner> _logger;

    public BackgroundAgentRunner(
        IConversationStore store,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentEnvironment environment,
        AgentConfig agentConfig,
        SystemJobStateStore jobStateStore,
        DeliveryRouter deliveryRouter,
        ILogger<BackgroundAgentRunner> logger)
    {
        _store = store;
        _textProviderResolver = textProviderResolver;
        _environment = environment;
        _agentConfig = agentConfig;
        _jobStateStore = jobStateStore;
        _deliveryRouter = deliveryRouter;
        _logger = logger;
    }

    /// <summary>
    /// Apply the gates from BACKGROUND.md. Returns true only when all are satisfied. The
    /// time-of-day window is owned by the cron ("*/15 6-21 * * *"); this method covers the master
    /// switch, configuration sanity, and the two interval gates.
    /// </summary>
    public Task<bool> ShouldRunAsync(DateTimeOffset now)
    {
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
            _logger.LogDebug("Background agent gated: main conversation '{ConversationId}' not found",
                _agentConfig.MainConversationId);
            return Task.FromResult(false);
        }

        // Gate 1: minimum interval since our previous successful run.
        var jobState = _jobStateStore.GetOrCreate(JobName);
        if (jobState.LastRunAt is { } lastRun && now - lastRun < MinSinceLastRun)
        {
            _logger.LogDebug("Background agent gated: only {Elapsed} since last run (need {Required})",
                now - lastRun, MinSinceLastRun);
            return Task.FromResult(false);
        }

        // Gate 2: minimum quiet period. Don't interrupt an active conversation.
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
    /// Run one heartbeat: hand the nudge to the provider as an ephemeral message, let the agent
    /// take a normal turn, and deliver the reply if there is one.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var mainConversationId = _agentConfig.MainConversationId;
        if (string.IsNullOrWhiteSpace(mainConversationId))
        {
            _logger.LogWarning("Heartbeat skipped: AgentConfig.MainConversationId is not set");
            return;
        }

        var conversation = _store.Get(mainConversationId);
        if (conversation is null)
        {
            _logger.LogWarning("Heartbeat skipped: main conversation '{ConversationId}' not found", mainConversationId);
            return;
        }

        var nudge = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = mainConversationId,
            Role = "user",
            Content = BuildNudge(),
            Modality = MessageModality.Text
        };

        var provider = _textProviderResolver(conversation.TextProvider);
        var startedAt = DateTimeOffset.UtcNow;

        // Text providers run one or more tool-call rounds per turn, and re-declare their
        // fullContent accumulator INSIDE that round loop - TextDelta is yielded on every round,
        // including rounds that end in a tool call. Text emitted before a tool call is never
        // persisted as assistant content (only the final round's text is), so concatenating
        // across rounds here would deliver narration the provider itself discarded. Reset on
        // every ToolCallEvent so only the final round's text survives to delivery - this must
        // exactly match what the provider persisted as the assistant message.
        var reply = new StringBuilder();
        var suppressed = false;

        // persistUserMessage: false — the nudge is scaffolding, never conversation, so it must
        // never be written to the store in the first place. The provider appends it to the
        // in-memory message list it sends the LLM and nothing more; there is no cleanup step
        // because there is nothing to clean up, even if this throws or the process is killed
        // mid-turn.
        await foreach (var evt in provider.CompleteAsync(conversation, nudge, ct, persistUserMessage: false))
        {
            switch (evt)
            {
                case TextDelta delta:
                    reply.Append(delta.Content);
                    break;
                case ToolCallEvent:
                    // A tool call means this round's text was scratch, not the final reply.
                    reply.Clear();
                    break;
                case ResponseSuppressed:
                    // The provider already deleted the whole turn from history via the "[]"
                    // sentinel. Trust the event, not a re-derived guess from accumulated text.
                    suppressed = true;
                    break;
            }
        }

        if (suppressed)
        {
            _logger.LogInformation("Heartbeat silent in {Ms}ms - agent had nothing to say",
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            return;
        }

        var text = reply.ToString();

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Heartbeat produced no text; nothing to deliver");
            return;
        }

        try
        {
            // Re-fetch in case channel binding shifted during the completion.
            var current = _store.Get(mainConversationId) ?? conversation;
            await _deliveryRouter.DeliverAsync(current, text, ct);
            _logger.LogInformation("Heartbeat spoke: delivered {Length}-char message in {Ms}ms",
                text.Length, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // The reply is already in history; a delivery failure must not fail the job.
            _logger.LogError(ex, "Heartbeat delivery failed for conversation '{ConversationId}'", mainConversationId);
        }
    }

    /// <summary>
    /// Build the ephemeral nudge. BACKGROUND.md is loaded fresh each run and carried inline,
    /// because the main conversation's system prompt does not include it (SystemPromptBuilder only
    /// loaded it for the retired "background"-source conversation).
    /// </summary>
    private string BuildNudge()
    {
        var path = Path.Combine(_environment.DataPath, "BACKGROUND.md");
        var instructions = File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine(NudgeMarker);
        sb.AppendLine();
        if (instructions.Length > 0)
        {
            sb.AppendLine(instructions);
            sb.AppendLine();
        }
        sb.Append("Reflect on the conversation above. If there is nothing genuinely worth saying, "
                  + "reply with exactly [] and nothing else.");
        return sb.ToString();
    }
}
