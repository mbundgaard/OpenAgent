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

    /// <summary>Sends a text message with HTML parse mode. Returns the Telegram message ID.</summary>
    Task<int> SendHtmlAsync(ChatId chatId, string html, CancellationToken ct);

    /// <summary>Sends a plain text message (no parse mode). Returns the Telegram message ID.</summary>
    Task<int> SendTextAsync(ChatId chatId, string text, CancellationToken ct);

    /// <summary>Sends a message draft that updates in-place (Bot API 9.3+).</summary>
    Task<DraftResult> SendDraftAsync(ChatId chatId, long draftId, string text, string? parseMode, CancellationToken ct);
}

/// <summary>
/// Result of a sendMessageDraft call. Contains success/failure info and rate limit details.
/// </summary>
public sealed class DraftResult
{
    public bool Ok { get; init; }
    public int StatusCode { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public string? Description { get; init; }

    public static DraftResult Success() => new() { Ok = true, StatusCode = 200 };
}
