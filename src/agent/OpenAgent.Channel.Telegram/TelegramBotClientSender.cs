using System.Net.Http.Json;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Production implementation of <see cref="ITelegramSender"/> that delegates to <see cref="ITelegramBotClient"/>.
/// Uses raw HTTP for sendMessageDraft (not yet in Telegram.Bot NuGet).
/// </summary>
public sealed class TelegramBotClientSender : ITelegramSender
{
    private readonly ITelegramBotClient _botClient;
    private readonly HttpClient _httpClient;
    private readonly string _botToken;

    public TelegramBotClientSender(ITelegramBotClient botClient, string botToken)
    {
        _botClient = botClient;
        _botToken = botToken;
        _httpClient = new HttpClient { BaseAddress = new Uri($"https://api.telegram.org/bot{_botToken}/") };
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

    /// <inheritdoc />
    public async Task<DraftResult> SendDraftAsync(ChatId chatId, long draftId, string text, CancellationToken ct)
    {
        // Raw HTTP — sendMessageDraft is not yet in Telegram.Bot NuGet
        var payload = new { chat_id = chatId.Identifier, draft_id = draftId, text };
        var response = await _httpClient.PostAsJsonAsync("sendMessageDraft", payload, ct);

        var statusCode = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
            return DraftResult.Success();

        // Read response body for error details and retry_after
        int? retryAfter = null;
        string? description = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("description", out var desc))
                description = desc.GetString();
            if (root.TryGetProperty("parameters", out var parameters)
                && parameters.TryGetProperty("retry_after", out var ra))
                retryAfter = ra.GetInt32();
        }
        catch
        {
            // Best-effort parse — don't fail if response body is unexpected
        }

        return new DraftResult
        {
            Ok = false,
            StatusCode = statusCode,
            RetryAfterSeconds = retryAfter,
            Description = description
        };
    }
}
