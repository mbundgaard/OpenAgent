namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Checks whether a WhatsApp chat JID is in the allowlist.
/// Empty allowlist = all chats allowed (open by default).
/// </summary>
public sealed class WhatsAppAccessControl
{
    private readonly HashSet<string> _allowedChatIds;

    public WhatsAppAccessControl(IEnumerable<string> allowedChatIds)
    {
        _allowedChatIds = new HashSet<string>(allowedChatIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns true if the chat JID is allowed. Empty list = allow all.</summary>
    public bool IsAllowed(string chatId) =>
        _allowedChatIds.Count == 0 || _allowedChatIds.Contains(chatId);
}
