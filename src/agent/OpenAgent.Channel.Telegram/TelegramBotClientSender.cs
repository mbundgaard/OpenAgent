using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Production implementation of <see cref="ITelegramSender"/> that delegates to <see cref="ITelegramBotClient"/>.
/// </summary>
public sealed class TelegramBotClientSender : ITelegramSender
{
    private readonly ITelegramBotClient _botClient;

    public TelegramBotClientSender(ITelegramBotClient botClient)
    {
        _botClient = botClient;
    }

    /// <inheritdoc />
    public async Task SendTypingAsync(ChatId chatId, CancellationToken ct)
    {
        await _botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task SendHtmlAsync(ChatId chatId, string html, CancellationToken ct)
    {
        await _botClient.SendMessage(chatId, html, parseMode: ParseMode.Html, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task SendTextAsync(ChatId chatId, string text, CancellationToken ct)
    {
        await _botClient.SendMessage(chatId, text, cancellationToken: ct);
    }
}
