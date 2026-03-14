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
/// Supports two response modes: streaming (sendMessageDraft) and batch (collect-then-send).
/// </summary>
public sealed class TelegramMessageHandler
{
    private const int TelegramMaxMessageLength = 4096;
    private const int MaxSendRetries = 3;
    private const int DraftIntervalMs = 300;

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
    private readonly bool _streamResponses;
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
        _streamResponses = options.StreamResponses;
        _logger = logger;
    }

    /// <summary>
    /// Processes a Telegram update. Filters for private text messages from allowed users,
    /// runs LLM completion, and sends the reply (streaming or batch based on config).
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

        _logger?.LogInformation("Message from user {UserId} in chat {ChatId}: {Text}", userId, chatId, userText);

        // Get or create conversation using the connection's configured ID
        var conversation = _store.GetOrCreate(_conversationId, "telegram", ConversationType.Text);

        // Get LLM completion events
        var events = _textProvider.CompleteAsync(conversation, userText, ct);

        // Route to streaming or batch based on config
        var mode = _streamResponses ? "streaming" : "batch";
        _logger?.LogInformation("Starting {Mode} response for chat {ChatId}", mode, chatId);

        if (_streamResponses)
            await StreamResponseAsync(sender, chatId, events, ct);
        else
            await CollectAndSendAsync(sender, chatId, events, ct);
    }

    /// <summary>
    /// Streams partial responses via sendMessageDraft using producer/consumer pattern.
    /// Producer: the foreach loop appends tokens to a shared buffer.
    /// Consumer: a background task sends drafts at a fixed interval.
    /// </summary>
    private async Task StreamResponseAsync(
        ITelegramSender sender, long chatId, IAsyncEnumerable<CompletionEvent> events, CancellationToken ct)
    {
        var buffer = new StringBuilder();
        var bufferLock = new object();
        var draftId = GenerateDraftId();
        var producerDone = false;
        var draftsSent = 0;
        var lastSentLength = 0;

        _logger?.LogInformation("Stream started for chat {ChatId}, draftId={DraftId}, interval={IntervalMs}ms",
            chatId, draftId, DraftIntervalMs);

        // Consumer: background task that sends drafts at a fixed interval
        var consumerTask = Task.Run(async () =>
        {
            var backoffUntil = DateTime.MinValue;

            while (true)
            {
                await Task.Delay(DraftIntervalMs, ct);

                // Snapshot the buffer
                string snapshot;
                bool done;
                lock (bufferLock)
                {
                    snapshot = buffer.ToString();
                    done = producerDone;
                }

                // Skip if nothing new to send
                if (snapshot.Length == lastSentLength)
                {
                    if (done) break;
                    continue;
                }

                // Respect rate limit backoff
                if (DateTime.UtcNow < backoffUntil)
                {
                    _logger?.LogDebug("Draft skipped for chat {ChatId}, in backoff until {BackoffUntil:HH:mm:ss}",
                        chatId, backoffUntil);
                    if (done) break;
                    continue;
                }

                // Send draft
                var result = await sender.SendDraftAsync(chatId, draftId, snapshot, ct);

                if (result.Ok)
                {
                    draftsSent++;
                    lastSentLength = snapshot.Length;
                    _logger?.LogDebug("Draft #{DraftNum} sent for chat {ChatId}, {Length} chars",
                        draftsSent, chatId, snapshot.Length);
                }
                else
                {
                    var backoffSeconds = result.RetryAfterSeconds ?? 1;
                    backoffUntil = DateTime.UtcNow.AddSeconds(backoffSeconds);
                    _logger?.LogWarning(
                        "Draft failed for chat {ChatId}: HTTP {StatusCode}, \"{Description}\", retry_after={RetryAfter}s",
                        chatId, result.StatusCode, result.Description, result.RetryAfterSeconds);
                }

                if (done) break;
            }
        }, ct);

        // Producer: consume LLM tokens and append to buffer
        try
        {
            await foreach (var evt in events.WithCancellation(ct))
            {
                if (evt is not TextDelta delta) continue;
                lock (bufferLock)
                {
                    buffer.Append(delta.Content);
                }
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not been configured"))
        {
            _logger?.LogError(ex, "LLM provider not configured for chat {ChatId}", chatId);
            lock (bufferLock)
            {
                buffer.Clear();
                buffer.Append("LLM provider is not configured. Please configure it via the admin app in the AgentOS.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LLM completion failed for chat {ChatId}", chatId);
            lock (bufferLock)
            {
                buffer.Clear();
                buffer.Append($"Something went wrong: {ex.Message}");
            }
        }

        // Signal consumer that producer is done, then wait for it to finish
        lock (bufferLock) { producerDone = true; }
        try { await consumerTask; }
        catch (OperationCanceledException) { /* expected on cancellation */ }

        // Finalize: send the complete message (replaces the draft)
        string replyText;
        lock (bufferLock) { replyText = buffer.ToString(); }

        _logger?.LogInformation("Stream complete for chat {ChatId}: {DraftsSent} drafts sent, {ReplyLength} chars",
            chatId, draftsSent, replyText.Length);

        var chunks = TelegramMarkdownConverter.ChunkMarkdown(replyText, TelegramMaxMessageLength);

        foreach (var chunk in chunks)
        {
            var html = TelegramMarkdownConverter.ToTelegramHtml(chunk);
            await SendWithRetryAsync(sender, chatId, html, chunk, ct);
        }

        _logger?.LogInformation("Final message sent for chat {ChatId}, {ChunkCount} chunk(s)", chatId, chunks.Count);
    }

    /// <summary>
    /// Collects all tokens, then sends the complete response (original behavior).
    /// </summary>
    private async Task CollectAndSendAsync(
        ITelegramSender sender, long chatId, IAsyncEnumerable<CompletionEvent> events, CancellationToken ct)
    {
        string replyText;
        try
        {
            var sb = new StringBuilder();
            await foreach (var evt in events.WithCancellation(ct))
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
        _logger?.LogInformation("Batch complete for chat {ChatId}: {ReplyLength} chars", chatId, replyText.Length);

        var chunks = TelegramMarkdownConverter.ChunkMarkdown(replyText, TelegramMaxMessageLength);

        foreach (var chunk in chunks)
        {
            var html = TelegramMarkdownConverter.ToTelegramHtml(chunk);
            await SendWithRetryAsync(sender, chatId, html, chunk, ct);
        }

        _logger?.LogInformation("Final message sent for chat {ChatId}, {ChunkCount} chunk(s)", chatId, chunks.Count);
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
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "HTML send failed for chat {ChatId}, falling back to plain text", chatId);
        }

        // Fallback: send as plain text with retries and exponential backoff
        for (var attempt = 0; attempt < MaxSendRetries; attempt++)
        {
            try
            {
                await sender.SendTextAsync(chatId, plainText, ct);
                return;
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

    /// <summary>Generates a non-zero draft ID using a random int64.</summary>
    private static long GenerateDraftId() => Random.Shared.NextInt64(1, long.MaxValue);
}
