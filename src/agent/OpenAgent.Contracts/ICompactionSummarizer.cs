using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

/// <summary>
/// Generates a compaction summary from conversation messages.
/// Called by the conversation store when compaction is triggered.
/// </summary>
public interface ICompactionSummarizer
{
    /// <summary>
    /// Summarizes messages into a structured context with topic grouping, timestamps, and message references.
    /// When <paramref name="existingContext"/> is null, the summarizer uses an Initial prompt; otherwise
    /// it uses an Update prompt that merges the new messages into the existing summary.
    /// </summary>
    /// <param name="existingContext">Previous compaction summary to merge into, or null for first compaction.</param>
    /// <param name="messages">Messages to compact — includes user, assistant, and tool call messages.</param>
    /// <param name="customInstructions">Optional focus hint, e.g. from a manual /compact call.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The structured summary to store as Conversation.Context.</returns>
    Task<CompactionResult> SummarizeAsync(
        string? existingContext,
        IReadOnlyList<Message> messages,
        string? customInstructions = null,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a compaction summarization.
/// </summary>
public sealed class CompactionResult
{
    /// <summary>Structured summary with topic grouping, timestamps, and [ref: ...] message references.</summary>
    public required string Context { get; init; }
}
