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

    /// <summary>
    /// Test constructor — injects a pre-built <see cref="HttpClient"/> so a stubbed
    /// <see cref="HttpMessageHandler"/> can exercise the raw-HTTP send methods without a live bot.
    /// </summary>
    internal TelegramBotClientSender(HttpClient httpClient)
    {
        _botClient = null!;
        _botToken = string.Empty;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task SendTypingAsync(ChatId chatId, CancellationToken ct)
    {
        await _botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task<int> SendHtmlAsync(ChatId chatId, string html, CancellationToken ct)
    {
        var msg = await _botClient.SendMessage(chatId, html, parseMode: ParseMode.Html, cancellationToken: ct);
        return msg.MessageId;
    }

    /// <inheritdoc />
    public async Task<int> SendTextAsync(ChatId chatId, string text, CancellationToken ct)
    {
        var msg = await _botClient.SendMessage(chatId, text, cancellationToken: ct);
        return msg.MessageId;
    }

    /// <inheritdoc />
    public async Task<DraftResult> SendDraftAsync(ChatId chatId, long draftId, string text, string? parseMode, CancellationToken ct)
    {
        // Raw HTTP — sendMessageDraft is not yet in Telegram.Bot NuGet
        // Build payload with optional parse_mode
        object payload = parseMode is not null
            ? new { chat_id = chatId.Identifier, draft_id = draftId, text, parse_mode = parseMode }
            : new { chat_id = chatId.Identifier, draft_id = draftId, text };
        var response = await _httpClient.PostAsJsonAsync("sendMessageDraft", payload, ct);

        if (response.IsSuccessStatusCode)
            return DraftResult.Success();

        return await ParseDraftFailureAsync(response, ct);
    }

    /// <inheritdoc />
    public async Task<int> SendRichMarkdownAsync(ChatId chatId, string markdown, CancellationToken ct)
    {
        // Raw HTTP — sendRichMessage is not yet in Telegram.Bot NuGet
        var payload = new
        {
            chat_id = chatId.Identifier ?? (object)chatId.Username!,
            rich_message = new { markdown }
        };
        var response = await _httpClient.PostAsJsonAsync("sendRichMessage", payload, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt32();
    }

    /// <inheritdoc />
    public async Task<DraftResult> SendRichMarkdownDraftAsync(ChatId chatId, long draftId, string markdown, CancellationToken ct)
    {
        // Raw HTTP — sendRichMessageDraft is not yet in Telegram.Bot NuGet
        var payload = new
        {
            chat_id = chatId.Identifier,
            draft_id = draftId,
            rich_message = new { markdown }
        };
        var response = await _httpClient.PostAsJsonAsync("sendRichMessageDraft", payload, ct);

        if (response.IsSuccessStatusCode)
            return DraftResult.Success();

        return await ParseDraftFailureAsync(response, ct);
    }

    /// <summary>
    /// Best-effort parse of a failed draft response into a <see cref="DraftResult"/>,
    /// extracting the Telegram <c>description</c> and <c>parameters.retry_after</c> rate-limit hint.
    /// </summary>
    private async Task<DraftResult> ParseDraftFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var statusCode = (int)response.StatusCode;

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
