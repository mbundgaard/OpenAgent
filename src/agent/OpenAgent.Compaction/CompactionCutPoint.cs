using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Chooses where to split conversation history for compaction. Walks backward from
/// the newest message accumulating estimated tokens until <c>keepRecentTokens</c> is
/// reached, then snaps to the nearest earlier user (or assistant-without-tool_calls)
/// boundary. Never cuts inside a tool-call round — the assistant's tool_calls and
/// its trailing tool result messages stay on the same side of the cut.
/// </summary>
public static class CompactionCutPoint
{
    /// <summary>
    /// Returns the index of the first kept message, or null if no cut is warranted
    /// (i.e. the entire history already fits in <paramref name="keepRecentTokens"/>).
    /// </summary>
    public static int? Find(IReadOnlyList<Message> messages, int keepRecentTokens)
    {
        if (messages.Count == 0) return null;

        // Walk backward, accumulating tokens. Remember the index at which we first cross
        // the budget — the cut must be at or before this index.
        var accumulated = 0;
        var crossIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            accumulated += TokenEstimator.EstimateMessage(messages[i]);
            if (accumulated >= keepRecentTokens)
            {
                crossIndex = i;
                break;
            }
        }

        if (crossIndex < 0) return null; // everything fits

        // Prefer snapping to a user message — keeps whole turns (user + assistant +
        // tool results) intact. A cut inside a turn strands its contextually-linked
        // parts on opposite sides, which confuses the summarizer.
        for (var i = crossIndex; i >= 0; i--)
        {
            if (messages[i].Role == "user")
                return i;
        }

        // Fallback: assistant without tool_calls. Rare in practice — conversations
        // normally start with a user message — but defensively handle it. Never cut
        // at a tool result (would split a tool-call round) or an assistant with
        // tool_calls (would strand its tool results on the other side).
        for (var i = crossIndex; i >= 0; i--)
        {
            if (messages[i].Role == "assistant" && string.IsNullOrEmpty(messages[i].ToolCalls))
                return i;
        }

        return null; // no valid boundary found — don't compact
    }
}
