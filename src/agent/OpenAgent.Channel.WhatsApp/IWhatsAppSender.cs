namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Abstraction for sending messages to WhatsApp via the Node bridge process.
/// </summary>
public interface IWhatsAppSender
{
    /// <summary>Sends a "composing" presence indicator to the chat.</summary>
    Task SendComposingAsync(string chatId);

    /// <summary>
    /// Sends a text message to the chat and returns the resulting Baileys stanza ID.
    /// Returns null when the send failed or timed out.
    /// </summary>
    Task<string?> SendTextAsync(string chatId, string text);
}
