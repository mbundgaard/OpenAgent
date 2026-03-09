namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Configuration for the Telegram channel, bound from the "Telegram" config section.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>Telegram Bot API token from BotFather.</summary>
    public string? BotToken { get; set; }

    /// <summary>
    /// Telegram user IDs allowed to interact with the bot. Empty = all users blocked.
    /// </summary>
    public List<long> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Channel mode: "Polling" (default, for local dev) or "Webhook" (for production).
    /// </summary>
    public string Mode { get; set; } = "Polling";

    /// <summary>
    /// Public HTTPS URL for Telegram to send webhook updates to. Required when Mode is "Webhook".
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Secret token for webhook validation. Auto-generated if not set.
    /// </summary>
    public string? WebhookSecret { get; set; }
}
