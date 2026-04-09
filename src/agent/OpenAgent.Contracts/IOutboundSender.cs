namespace OpenAgent.Contracts;

/// <summary>
/// Marks a channel provider as capable of proactive messaging — sending a message that
/// wasn't prompted by an incoming user message. Until this interface, channels were strictly
/// inbound (user → agent); scheduled tasks needed the reverse (agent → user), which requires
/// reusing a running provider's authenticated session.
///
/// Implemented opt-in by channel providers: if a new channel type doesn't support outbound,
/// simply don't implement it and DeliveryRouter will warn and fall back to silent delivery.
/// Kept intentionally minimal (text-only) — will likely grow to support markdown, images, and
/// structured messages as channel feature needs dictate.
/// </summary>
public interface IOutboundSender
{
    /// <summary>Sends a text message to the specified chat.</summary>
    Task SendMessageAsync(string chatId, string text, CancellationToken ct = default);
}
