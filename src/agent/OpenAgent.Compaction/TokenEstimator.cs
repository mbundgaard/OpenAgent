using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Conservative character-based token estimator for compaction cut-point logic.
/// Uses a chars/4 heuristic with per-role specializations and a ceiling on tool
/// results so one huge tool output doesn't dominate the walk.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Approximate character count per token (inverse of chars/4).</summary>
    private const int CharsPerToken = 4;

    /// <summary>
    /// Maximum tokens a single tool result contributes to the cut-point walk.
    /// Prevents a huge tool output from locking the cut point in place far from
    /// the newest messages.
    /// </summary>
    public const int ToolResultTokenCap = 12_500; // ~50k chars worth of output

    public static int EstimateMessage(Message message)
    {
        var chars = message.Role switch
        {
            "user" => message.Content?.Length ?? 0,
            "assistant" => (message.Content?.Length ?? 0) + (message.ToolCalls?.Length ?? 0),
            "tool" => (message.FullToolResult ?? message.Content)?.Length ?? 0,
            _ => message.Content?.Length ?? 0
        };

        var tokens = (int)Math.Ceiling(chars / (double)CharsPerToken);

        if (message.Role == "tool" && tokens > ToolResultTokenCap)
            tokens = ToolResultTokenCap;

        return tokens;
    }
}
