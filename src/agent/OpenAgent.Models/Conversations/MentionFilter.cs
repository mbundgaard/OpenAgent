namespace OpenAgent.Models.Conversations;

/// <summary>
/// Decides whether an incoming user message should be processed based on
/// the conversation's <see cref="Conversation.MentionNames"/> list.
/// </summary>
public static class MentionFilter
{
    /// <summary>
    /// Returns true when the message should be processed. A conversation with
    /// no mention names (null or empty) accepts any text. Otherwise the text
    /// must contain at least one non-empty name as a case-insensitive substring.
    /// </summary>
    public static bool ShouldAccept(Conversation conversation, string userText)
    {
        if (conversation.MentionNames is null || conversation.MentionNames.Count == 0)
            return true;

        foreach (var name in conversation.MentionNames)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            if (userText.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
