namespace OpenAgent.Models.Conversations;

/// <summary>
/// Decides whether an incoming user message should be processed based on
/// the conversation's <see cref="Conversation.MentionFilter"/> list.
/// </summary>
public static class MentionMatcher
{
    /// <summary>
    /// Returns true when the message should be processed. A conversation with
    /// no mention filter (null or empty) accepts any text. Otherwise the text
    /// must contain at least one non-empty name as a case-insensitive substring.
    /// </summary>
    public static bool ShouldAccept(Conversation conversation, string userText)
    {
        if (conversation.MentionFilter is null || conversation.MentionFilter.Count == 0)
            return true;

        foreach (var name in conversation.MentionFilter)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            if (userText.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
