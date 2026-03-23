namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Abstraction for sending messages to WhatsApp via the Node bridge process.
/// </summary>
public interface IWhatsAppSender
{
    /// <summary>Sends a "composing" presence indicator to the chat.</summary>
    Task SendComposingAsync(string chatId);

    /// <summary>Sends a text message to the chat.</summary>
    Task SendTextAsync(string chatId, string text);
}
