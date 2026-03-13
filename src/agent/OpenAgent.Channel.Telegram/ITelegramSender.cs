using Telegram.Bot.Types;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Thin abstraction over Telegram bot client for sending messages and actions.
/// Enables unit testing without faking the full ITelegramBotClient interface.
/// </summary>
public interface ITelegramSender
{
    /// <summary>Sends a typing indicator to the specified chat.</summary>
    Task SendTypingAsync(ChatId chatId, CancellationToken ct);

    /// <summary>Sends a text message with HTML parse mode.</summary>
    Task SendHtmlAsync(ChatId chatId, string html, CancellationToken ct);

    /// <summary>Sends a plain text message (no parse mode).</summary>
    Task SendTextAsync(ChatId chatId, string text, CancellationToken ct);
}
