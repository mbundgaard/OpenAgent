using System.Text;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Core handler that processes Telegram updates: filters messages, routes to
/// conversations, calls the LLM, and sends replies via <see cref="ITelegramSender"/>.
/// </summary>
public sealed class TelegramMessageHandler
{
    private const int TelegramMaxMessageLength = 4096;
    private const int MaxSendRetries = 3;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];

    private readonly IConversationStore _store;
    private readonly ILlmTextProvider _textProvider;
    private readonly string _conversationId;
    private readonly TelegramAccessControl _accessControl;
    private readonly ILogger<TelegramMessageHandler>? _logger;

    public TelegramMessageHandler(
        IConversationStore store,
        ILlmTextProvider textProvider,
        string conversationId,
        TelegramOptions options,
        ILogger<TelegramMessageHandler>? logger = null)
    {
        _store = store;
        _textProvider = textProvider;
        _conversationId = conversationId;
        _accessControl = new TelegramAccessControl(options.AllowedUserIds);
        _logger = logger;
    }

    /// <summary>
    /// Processes a Telegram update. Filters for private text messages from allowed users,
    /// runs LLM completion, and sends the reply.
    /// </summary>
    public async Task HandleUpdateAsync(ITelegramSender sender, Update update, CancellationToken ct)
    {
        // Filter: only handle text messages in private chats from known users
        if (update.Message is not { Text: not null, Chat.Type: ChatType.Private, From: not null } message)
            return;

        var chatId = message.Chat.Id;
        var userId = message.From!.Id;
        var userText = message.Text!;

        // Access control check — silently ignore unauthorized users
        if (!_accessControl.IsAllowed(userId))
        {
            _logger?.LogWarning("Blocked message from unauthorized user {UserId}", userId);
            return;
        }

        // Send typing indicator (best-effort, don't fail the whole flow)
        try
        {
            await sender.SendTypingAsync(chatId, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send typing indicator to chat {ChatId}", chatId);
        }

        // Get or create conversation using the connection's configured ID
        var conversation = _store.GetOrCreate(_conversationId, "telegram", ConversationType.Text);

        // Run LLM completion and collect text
        string replyText;
        try
        {
            var sb = new StringBuilder();
            await foreach (var evt in _textProvider.CompleteAsync(conversation, userText, ct))
            {
                if (evt is TextDelta delta)
                    sb.Append(delta.Content);
            }

            replyText = sb.ToString();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not been configured"))
        {
            _logger?.LogError(ex, "LLM provider not configured for chat {ChatId}", chatId);
            replyText = "LLM provider is not configured. Please configure it via the admin app in the AgentOS.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LLM completion failed for chat {ChatId}", chatId);
            replyText = $"Something went wrong: {ex.Message}";
        }

        // Chunk the response and send each chunk
        var chunks = TelegramMarkdownConverter.ChunkMarkdown(replyText, TelegramMaxMessageLength);

        foreach (var chunk in chunks)
        {
            var html = TelegramMarkdownConverter.ToTelegramHtml(chunk);
            await SendWithRetryAsync(sender, chatId, html, chunk, ct);
        }
    }

    /// <summary>
    /// Sends a message with retry logic. Tries HTML first, falls back to plain text on failure.
    /// Retries up to <see cref="MaxSendRetries"/> times with exponential backoff.
    /// </summary>
    private async Task SendWithRetryAsync(
        ITelegramSender sender, long chatId, string html, string plainText, CancellationToken ct)
    {
        // Try sending as HTML first
        try
        {
            await sender.SendHtmlAsync(chatId, html, ct);
            return; // success
        }
        catch (Exception ex)
        {
            // HTML failed — fall back to plain text with retries
            _logger?.LogWarning(ex, "HTML send failed for chat {ChatId}, falling back to plain text", chatId);
        }

        // Fallback: send as plain text with retries and exponential backoff
        for (var attempt = 0; attempt < MaxSendRetries; attempt++)
        {
            try
            {
                await sender.SendTextAsync(chatId, plainText, ct);
                return; // success
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Plain text send attempt {Attempt} failed for chat {ChatId}", attempt + 1, chatId);

                if (attempt < MaxSendRetries - 1)
                    await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        _logger?.LogError("All send attempts exhausted for chat {ChatId}", chatId);
    }
}
