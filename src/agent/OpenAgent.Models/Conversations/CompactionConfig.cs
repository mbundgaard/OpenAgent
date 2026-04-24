namespace OpenAgent.Models.Conversations;

/// <summary>
/// Configuration for conversation compaction thresholds.
/// </summary>
public sealed class CompactionConfig
{
    /// <summary>Model's context window size in tokens.</summary>
    public int MaxContextTokens { get; init; } = 400_000;

    /// <summary>Trigger compaction at this percentage of MaxContextTokens.</summary>
    public int CompactionTriggerPercent { get; init; } = 70;

    /// <summary>Number of recent message pairs to keep uncompacted.</summary>
    /// <remarks>Deprecated — superseded by <see cref="KeepRecentTokens"/> in PR 2 Task 8.
    /// Scheduled for removal after the cut-point switch lands.</remarks>
    public int KeepLatestMessagePairs { get; init; } = 5;

    /// <summary>
    /// Target size of the uncompacted tail in tokens. The cut point is the nearest
    /// user/assistant boundary (never a tool result) such that the tail estimates to
    /// at least this many tokens. Replaces <see cref="KeepLatestMessagePairs"/>.
    /// </summary>
    public int KeepRecentTokens { get; init; } = 20_000;

    /// <summary>Computed trigger threshold in tokens.</summary>
    public int TriggerThreshold => MaxContextTokens * CompactionTriggerPercent / 100;
}
