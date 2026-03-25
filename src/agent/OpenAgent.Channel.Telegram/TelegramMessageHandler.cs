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
    private readonly IConnectionStore _connectionStore;
    private readonly ILlmTextProvider _textProvider;
    private readonly string _connectionId;
    private readonly string _provider;
    private readonly string _model;
    private readonly bool _streamResponses;
    private readonly bool _showThinking;
    private readonly ILogger<TelegramMessageHandler>? _logger;

    public TelegramMessageHandler(
        IConversationStore store,
        IConnectionStore connectionStore,
        ILlmTextProvider textProvider,
        string connectionId,
        string provider,
        string model,
        TelegramOptions options,
        ILogger<TelegramMessageHandler>? logger = null)
    {
        _store = store;
        _connectionStore = connectionStore;
        _textProvider = textProvider;
        _connectionId = connectionId;
        _provider = provider;
        _model = model;
        _streamResponses = options.StreamResponses;
        _showThinking = options.ShowThinking;
        _logger = logger;
    }

    /// <summary>
    /// Processes a Telegram update. Filters for private text messages from allowed users,
    /// runs LLM completion, and sends the reply (streaming or batch based on config).
    /// </summary>
    public async Task HandleUpdateAsync(ITelegramSender sender, Update update, CancellationToken ct)
    {
        // Filter: handle text messages in private, group, and supergroup chats from known users
        if (update.Message is not { Text: not null, From: not null } message
            || message.Chat.Type is not (ChatType.Private or ChatType.Group or ChatType.Supergroup))
        {
            _logger?.LogDebug("Update {UpdateId} filtered out: not a supported text message (Type={Type}, HasText={HasText})",
                update.Id, update.Message?.Chat?.Type, update.Message?.Text is not null);
            return;
        }

        var chatId = message.Chat.Id;
        var userId = message.From!.Id;
        var userText = message.Text!;

        _logger?.LogDebug("Handler received message from user {UserId} in chat {ChatId}", userId, chatId);

        // Derive conversation ID from connection + chat — each chat gets its own conversation
        var derivedConversationId = $"telegram:{_connectionId}:{chatId}";

        // Conversation gating — check if conversation exists or if new ones are allowed
        var existing = _store.Get(derivedConversationId);
        if (existing is null)
        {
            var connection = _connectionStore.Load(_connectionId);
            if (connection is null || !connection.AllowNewConversations)
            {
                _logger?.LogDebug("New conversation from chat {ChatId} dropped — new conversations not allowed", chatId);
                return;
            }

            // Auto-lock: disable new conversations after the first one is created
            connection.AllowNewConversations = false;
            _connectionStore.Save(connection);
            _logger?.LogInformation("First conversation created for connection {ConnectionId}, auto-locked new conversations", _connectionId);
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

        // Get or create conversation
        var conversation = _store.GetOrCreate(derivedConversationId, "telegram", ConversationType.Text, _provider, _model);

        // Build user message with Telegram message ID and optional reply-to reference
        var userMessage = new Models.Conversations.Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = derivedConversationId,
            Role = "user",
            Content = userText,
            ChannelMessageId = message.MessageId.ToString(),
            ReplyToChannelMessageId = message.ReplyToMessage?.MessageId.ToString()
        };

        // Get LLM completion events
        var events = _textProvider.CompleteAsync(conversation, userMessage, ct);

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
        var producerDone = false;
        var draftsSent = 0;
        string? assistantMessageId = null;

        // Thinking state — tool lines collected per round, sent as blockquote before response text
        var toolLines = new List<string>();
        var pendingToolArgs = new Dictionary<string, string>();

        var draftId = GenerateDraftId();
        _logger?.LogInformation("Stream started for chat {ChatId}, draftId={DraftId}, interval={IntervalMs}ms",
            chatId, draftId, DraftIntervalMs);

        // Consumer: background task that sends drafts at a fixed interval
        var consumerTask = RunDraftConsumerAsync(sender, chatId, draftId, buffer, bufferLock,
            () => producerDone, d => draftsSent = d, ct);

        // Producer: consume LLM events, collect tool lines, buffer response text
        try
        {
            await foreach (var evt in events.WithCancellation(ct))
            {
                switch (evt)
                {
                    case ToolCallEvent toolCall:
                        if (_showThinking)
                            pendingToolArgs[toolCall.ToolCallId] = FormatToolArgs(toolCall.Arguments);
                        _logger?.LogDebug("Tool call: {Name}({Args})", toolCall.Name, toolCall.Arguments);
                        break;

                    case ToolResultEvent toolResult:
                        if (_showThinking)
                            toolLines.Add(FormatToolLine(toolResult, pendingToolArgs));
                        _logger?.LogDebug("Tool result: {Name} -> {Length} chars", toolResult.Name, toolResult.Result.Length);
                        break;

                    case TextDelta delta:
                        // Send any accumulated tool lines as a thinking message before text
                        if (toolLines.Count > 0)
                        {
                            await SendThinkingMessageAsync(sender, chatId, toolLines, ct);
                            toolLines.Clear();
                            pendingToolArgs.Clear();
                        }
                        lock (bufferLock) { buffer.Append(delta.Content); }
                        break;

                    case AssistantMessageSaved saved:
                        assistantMessageId = saved.MessageId;
                        break;
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

        await SendFinalResponseAsync(sender, chatId, replyText, assistantMessageId, ct);
    }

    /// <summary>
    /// Background task that sends draft updates at a fixed interval.
    /// Snapshots the shared buffer and sends when new content is available.
    /// </summary>
    private async Task RunDraftConsumerAsync(
        ITelegramSender sender, long chatId, long draftId,
        StringBuilder buffer, object bufferLock,
        Func<bool> isProducerDone, Action<int> setDraftsSent,
        CancellationToken ct)
    {
        var backoffUntil = DateTime.MinValue;
        var lastSentLength = 0;
        var draftsSent = 0;

        await Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(DraftIntervalMs, ct);

                // Snapshot the buffer
                string snapshot;
                bool done;
                lock (bufferLock)
                {
                    snapshot = buffer.ToString();
                    done = isProducerDone();
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

                // Send draft — catch unexpected exceptions so the consumer stays alive
                DraftResult result;
                try
                {
                    result = await sender.SendDraftAsync(chatId, draftId, snapshot, null, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Draft send threw for chat {ChatId}, will retry next interval", chatId);
                    if (done) break;
                    continue;
                }

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

            setDraftsSent(draftsSent);
        }, ct);
    }

    /// <summary>
    /// Sends collected tool call lines as an HTML blockquote message.
    /// </summary>
    private async Task SendThinkingMessageAsync(
        ITelegramSender sender, long chatId, List<string> toolLines, CancellationToken ct)
    {
        var toolCount = toolLines.Count;
        var label = toolCount == 1 ? "1 tool call" : $"{toolCount} tool calls";
        var html = $"<blockquote><b>\u2699\ufe0f {label}</b>\n{string.Join("\n", toolLines)}</blockquote>";
        try
        {
            await sender.SendHtmlAsync(chatId, html, ct);
            _logger?.LogDebug("Thinking message sent for chat {ChatId}: {LineCount} tool(s)", chatId, toolCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send thinking message for chat {ChatId}", chatId);
        }
    }

    /// <summary>
    /// Formats a tool result into a single HTML line with checkmark/cross and primary argument.
    /// </summary>
    private static string FormatToolLine(ToolResultEvent toolResult, Dictionary<string, string> pendingToolArgs)
    {
        var ok = IsToolSuccess(toolResult.Result);
        var mark = ok ? "\u2713" : "\u2717";
        pendingToolArgs.TryGetValue(toolResult.ToolCallId, out var args);
        return string.IsNullOrEmpty(args)
            ? $"{mark} {toolResult.Name}"
            : $"{mark} {toolResult.Name}  <i>{System.Net.WebUtility.HtmlEncode(args)}</i>";
    }

    /// <summary>
    /// Collects all tokens, then sends the complete response (original behavior).
    /// </summary>
    private async Task CollectAndSendAsync(
        ITelegramSender sender, long chatId, IAsyncEnumerable<CompletionEvent> events, CancellationToken ct)
    {
        string replyText;
        string? assistantMessageId = null;
        var toolLines = new List<string>();
        var pendingToolArgs = new Dictionary<string, string>();
        try
        {
            var sb = new StringBuilder();
            await foreach (var evt in events.WithCancellation(ct))
            {
                switch (evt)
                {
                    case ToolCallEvent toolCall:
                        if (_showThinking)
                            pendingToolArgs[toolCall.ToolCallId] = FormatToolArgs(toolCall.Arguments);
                        break;
                    case ToolResultEvent toolResult:
                        if (_showThinking)
                            toolLines.Add(FormatToolLine(toolResult, pendingToolArgs));
                        break;
                    case TextDelta delta:
                        // Send any accumulated tool lines before text starts
                        if (toolLines.Count > 0)
                        {
                            await SendThinkingMessageAsync(sender, chatId, toolLines, ct);
                            toolLines.Clear();
                            pendingToolArgs.Clear();
                        }
                        sb.Append(delta.Content);
                        break;
                    case AssistantMessageSaved saved:
                        assistantMessageId = saved.MessageId;
                        break;
                }
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

        _logger?.LogInformation("Batch complete for chat {ChatId}: {ReplyLength} chars", chatId, replyText.Length);
        await SendFinalResponseAsync(sender, chatId, replyText, assistantMessageId, ct);
    }

    /// <summary>
    /// Chunks the response text, sends each chunk to Telegram, and updates the
    /// assistant message with the Telegram message ID for reply-to tracking.
    /// </summary>
    private async Task SendFinalResponseAsync(
        ITelegramSender sender, long chatId, string replyText, string? assistantMessageId, CancellationToken ct)
    {
        var chunks = TelegramMarkdownConverter.ChunkMarkdown(replyText, TelegramMaxMessageLength);

        int? telegramMessageId = null;
        foreach (var chunk in chunks)
        {
            var html = TelegramMarkdownConverter.ToTelegramHtml(chunk);
            var sentId = await SendWithRetryAsync(sender, chatId, html, chunk, ct);
            telegramMessageId ??= sentId; // Keep the first chunk's ID
        }

        // Update the assistant message with the Telegram message ID (first chunk's ID)
        if (assistantMessageId is not null && telegramMessageId is not null)
        {
            _store.UpdateChannelMessageId(assistantMessageId, telegramMessageId.Value.ToString());
            _logger?.LogDebug("Updated assistant message {MessageId} with Telegram message ID {TelegramMessageId}",
                assistantMessageId, telegramMessageId);
        }

        _logger?.LogInformation("Final message sent for chat {ChatId}, {ChunkCount} chunk(s)", chatId, chunks.Count);
    }

    /// <summary>
    /// Sends a message with retry logic. Tries HTML first, falls back to plain text on failure.
    /// Retries up to <see cref="MaxSendRetries"/> times with exponential backoff.
    /// Returns the Telegram message ID, or null if all attempts failed.
    /// </summary>
    private async Task<int?> SendWithRetryAsync(
        ITelegramSender sender, long chatId, string html, string plainText, CancellationToken ct)
    {
        // Try sending as HTML first
        try
        {
            return await sender.SendHtmlAsync(chatId, html, ct);
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
                return await sender.SendTextAsync(chatId, plainText, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Plain text send attempt {Attempt} failed for chat {ChatId}", attempt + 1, chatId);

                if (attempt < MaxSendRetries - 1)
                    await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        _logger?.LogError("All send attempts exhausted for chat {ChatId}", chatId);
        return null;
    }

    /// <summary>Generates a non-zero draft ID using a random int64.</summary>
    private static long GenerateDraftId() => Random.Shared.NextInt64(1, long.MaxValue);

    /// <summary>
    /// Extracts the primary argument from a tool call — the one that identifies
    /// what the tool is operating on (path, command, url, query, etc.).
    /// </summary>
    private static string FormatToolArgs(string argumentsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            // Try common primary argument names in priority order
            foreach (var key in new[] { "path", "command", "url", "query", "name", "file" })
            {
                if (root.TryGetProperty(key, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var value = prop.GetString() ?? "";
                    if (value.Length > 50) value = value[..47] + "...";
                    return value;
                }
            }

            // Fallback: first string property
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var value = prop.Value.GetString() ?? "";
                    if (value.Length > 50) value = value[..47] + "...";
                    return value;
                }
            }
        }
        catch { /* not JSON */ }

        return "";
    }

    /// <summary>Checks whether a tool result indicates success.</summary>
    private static bool IsToolSuccess(string result)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("success", out var success))
                return success.GetBoolean();
        }
        catch { /* not JSON */ }

        return true;
    }
}
