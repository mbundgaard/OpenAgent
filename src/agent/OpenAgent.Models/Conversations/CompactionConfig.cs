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
    public int KeepLatestMessagePairs { get; init; } = 5;

    /// <summary>Computed trigger threshold in tokens.</summary>
    public int TriggerThreshold => MaxContextTokens * CompactionTriggerPercent / 100;
}
