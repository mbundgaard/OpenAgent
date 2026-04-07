namespace OpenAgent.Contracts;

/// <summary>
/// Sends outbound messages to a channel. Implemented by channel providers
/// that support proactive messaging (e.g. Telegram, WhatsApp).
/// </summary>
public interface IOutboundSender
{
    /// <summary>Sends a text message to the specified chat.</summary>
    Task SendMessageAsync(string chatId, string text, CancellationToken ct = default);
}
