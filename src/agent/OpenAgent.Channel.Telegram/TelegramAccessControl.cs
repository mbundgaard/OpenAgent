namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Checks whether a Telegram user ID is in the allowlist.
/// Empty allowlist = all users blocked (secure by default).
/// </summary>
public sealed class TelegramAccessControl
{
    private readonly HashSet<long> _allowedUserIds;

    public TelegramAccessControl(IEnumerable<long> allowedUserIds)
    {
        _allowedUserIds = new HashSet<long>(allowedUserIds);
    }

    /// <summary>Returns true if the user ID is in the allowlist.</summary>
    public bool IsAllowed(long userId) => _allowedUserIds.Contains(userId);
}
