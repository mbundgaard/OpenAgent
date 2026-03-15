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
    /// </summary>
    /// <param name="existingContext">Previous compaction summary to roll into the new one, or null.</param>
    /// <param name="messages">Messages to compact — includes user, assistant, and tool call messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new structured summary to store as Conversation.Context.</returns>
    Task<CompactionResult> SummarizeAsync(string? existingContext, IReadOnlyList<Message> messages, CancellationToken ct = default);
}

/// <summary>
/// Result of a compaction summarization.
/// </summary>
public sealed class CompactionResult
{
    /// <summary>Structured summary with topic grouping, timestamps, and [ref: ...] message references.</summary>
    public required string Context { get; init; }

    /// <summary>Durable facts extracted for daily memory, if any.</summary>
    public IReadOnlyList<string> Memories { get; init; } = [];
}
