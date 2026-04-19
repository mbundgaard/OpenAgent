using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

/// <summary>
/// Reactive trigger for the context purge. Called by the conversation store at the end of a turn
/// when the conversation's <c>LastPromptTokens</c> crosses a configured threshold. The implementation
/// decides whether to actually run the purge (threshold check + any rate limiting); the store just
/// notifies. See docs/plans/2026-04-19-context-pruning-design.md.
/// </summary>
public interface IContextPruneTrigger
{
    /// <summary>
    /// Signal that the conversation has just had a turn persisted; run the purge if warranted.
    /// Implementations must not throw — any failure should be logged internally.
    /// </summary>
    void OnTurnPersisted(Conversation conversation);
}
