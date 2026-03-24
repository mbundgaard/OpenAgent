namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Checks whether a Telegram user ID is in the allowlist.
/// Empty allowlist = all users allowed (open by default).
/// </summary>
public sealed class TelegramAccessControl
{
    private readonly HashSet<long> _allowedUserIds;

    public TelegramAccessControl(IEnumerable<long> allowedUserIds)
    {
        _allowedUserIds = new HashSet<long>(allowedUserIds);
    }

    /// <summary>Returns true if the user ID is allowed. Empty list = allow all.</summary>
    public bool IsAllowed(long userId) =>
        _allowedUserIds.Count == 0 || _allowedUserIds.Contains(userId);
}
