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

    /// <summary>
    /// Target size of the uncompacted tail in tokens. The cut point is the nearest
    /// user/assistant boundary (never a tool result) such that the tail estimates to
    /// at least this many tokens.
    /// </summary>
    public int KeepRecentTokens { get; init; } = 20_000;

    /// <summary>Computed trigger threshold in tokens.</summary>
    public int TriggerThreshold => MaxContextTokens * CompactionTriggerPercent / 100;
}
