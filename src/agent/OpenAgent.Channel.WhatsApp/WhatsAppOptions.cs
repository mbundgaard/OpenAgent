namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Configuration for the WhatsApp channel, deserialized from a connection's config blob.
/// </summary>
public sealed class WhatsAppOptions
{
    /// <summary>
    /// Chat IDs allowed to interact with the bot.
    /// Accepts JID format: "+4512345678@s.whatsapp.net" for DMs, "120363xxx@g.us" for groups.
    /// Empty or missing = all chats blocked (secure by default).
    /// </summary>
    public List<string> AllowedChatIds { get; set; } = [];
}
