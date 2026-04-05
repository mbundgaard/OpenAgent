namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Configuration for the Telegram channel, deserialized from a connection's config blob.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>Telegram Bot API token from BotFather.</summary>
    public string? BotToken { get; set; }

    /// <summary>
    /// Telegram user IDs allowed to interact with the bot.
    /// Empty or missing = all users blocked (secure by default).
    /// </summary>
    public List<long> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Channel mode: "Polling" (default, for local dev) or "Webhook" (for production).
    /// </summary>
    public string Mode { get; set; } = "Polling";

    /// <summary>
    /// Base URL of the OpenAgent instance (e.g. "https://openagent-comput.azurewebsites.net").
    /// Required when Mode is "Webhook". The full webhook URL is computed automatically.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Auto-generated GUID identifying this connection's webhook endpoint.
    /// Generated on first start and persisted in the connection config.
    /// </summary>
    public string? WebhookId { get; set; }

    /// <summary>
    /// Secret token for webhook validation. Auto-generated if not set.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// When true, streams partial responses to the user via sendMessageDraft
    /// as the LLM generates tokens. When false, sends the complete response at once.
    /// Requires Bot API 9.3+. Defaults to true.
    /// </summary>
    public bool StreamResponses { get; set; } = true;

    /// <summary>
    /// When true, shows tool calls and results in the draft while the LLM is thinking.
    /// </summary>
    public bool ShowThinking { get; set; }

}
