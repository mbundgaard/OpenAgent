using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.ContextPruning;

/// <summary>
/// Drives the tool-round purge for conversations. Round-based: one transaction per conversation
/// nulls out old assistant ToolCalls and the matching tool-result children in lockstep. See
/// docs/plans/2026-04-19-context-pruning-design.md.
/// <para>
/// The conversation store is resolved lazily via <see cref="Lazy{T}"/> to break the otherwise
/// circular DI dependency: the store depends on <see cref="IContextPruneTrigger"/>, which is
/// satisfied by this service, which in turn needs the store for its purge calls.
/// </para>
/// </summary>
public sealed class ContextPruneService : IContextPruneTrigger
{
    private readonly Lazy<IConversationStore> _storeLazy;
    private readonly AgentConfig _agentConfig;
    private readonly CompactionConfig _compactionConfig;
    private readonly ILogger<ContextPruneService> _logger;

    private IConversationStore Store => _storeLazy.Value;

    public ContextPruneService(
        Lazy<IConversationStore> storeLazy,
        AgentConfig agentConfig,
        CompactionConfig compactionConfig,
        ILogger<ContextPruneService> logger)
    {
        _storeLazy = storeLazy;
        _agentConfig = agentConfig;
        _compactionConfig = compactionConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnTurnPersisted(Conversation conversation)
    {
        try
        {
            if (conversation.LastPromptTokens is not { } tokens) return;

            var thresholdPercent = Math.Clamp(_agentConfig.PurgeReactiveThresholdPercent, 1, 100);
            var threshold = _compactionConfig.MaxContextTokens * thresholdPercent / 100;
            if (tokens < threshold) return;

            _logger.LogInformation(
                "Reactive purge triggered for conversation {ConversationId}: LastPromptTokens={Tokens} >= threshold={Threshold}",
                conversation.Id, tokens, threshold);
            PurgeOne(conversation.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reactive context purge failed for conversation {ConversationId}", conversation.Id);
        }
    }

    /// <summary>
    /// Runs the purge against every conversation the store knows about. Errors on any one
    /// conversation are logged and do not block the others. Returns aggregate totals.
    /// </summary>
    public (int Conversations, int RoundsPurged, int ResultRowsPurged) PurgeAll()
    {
        var conversations = Store.GetAll();
        var totalRounds = 0;
        var totalResultRows = 0;
        var processed = 0;

        foreach (var c in conversations)
        {
            try
            {
                var (rounds, resultRows) = PurgeOne(c.Id);
                totalRounds += rounds;
                totalResultRows += resultRows;
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Context purge failed for conversation {ConversationId}; continuing", c.Id);
            }
        }

        _logger.LogInformation(
            "Context purge sweep: conversations={Conversations} rounds={Rounds} resultRows={ResultRows}",
            processed, totalRounds, totalResultRows);

        return (processed, totalRounds, totalResultRows);
    }

    /// <summary>
    /// Runs the purge against a single conversation. Intended for the reactive trigger at
    /// end of turn when the conversation's LastPromptTokens crosses the threshold.
    /// </summary>
    public (int RoundsPurged, int ResultRowsPurged) PurgeOne(string conversationId)
    {
        var keepLast = Math.Max(0, _agentConfig.PurgeKeepLast);
        var ageHours = Math.Max(1, _agentConfig.PurgeAgeCutoffHours);
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(ageHours);

        var (rounds, resultRows) = Store.PurgeOldToolRounds(conversationId, keepLast, cutoff);

        if (rounds > 0 || resultRows > 0)
        {
            _logger.LogInformation(
                "Context purge for conversation {ConversationId}: rounds={Rounds} resultRows={ResultRows} keepLast={KeepLast} ageHours={AgeHours}",
                conversationId, rounds, resultRows, keepLast, ageHours);
        }

        return (rounds, resultRows);
    }
}
