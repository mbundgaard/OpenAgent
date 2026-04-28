using System.Text;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Processes inbound WhatsApp messages: conversation gating, dedup, sender attribution
/// for groups, LLM completion, composing indicator, and response sending.
/// </summary>
public sealed class WhatsAppMessageHandler
{
    private const int WhatsAppMaxMessageLength = 4096;
    private const int DedupMaxEntries = 5000;
    private const int DedupEvictThreshold = 2500;
    private static readonly TimeSpan DedupTtl = TimeSpan.FromMinutes(20);

    private readonly IConversationStore _store;
    private readonly IConnectionStore _connectionStore;
    private readonly Func<string, ILlmTextProvider> _textProviderResolver;
    private readonly string _connectionId;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<WhatsAppMessageHandler>? _logger;

    // Dedup: message ID -> time first seen
    private readonly Dictionary<string, DateTime> _processedMessages = new();

    /// <summary>
    /// Creates a new WhatsAppMessageHandler.
    /// </summary>
    public WhatsAppMessageHandler(
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmTextProvider> textProviderResolver,
        string connectionId,
        AgentConfig agentConfig,
        ILogger<WhatsAppMessageHandler>? logger = null)
    {
        _store = store;
        _connectionStore = connectionStore;
        _textProviderResolver = textProviderResolver;
        _connectionId = connectionId;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    /// <summary>
    /// Processes a single inbound WhatsApp message event. Checks access control,
    /// deduplicates, sends composing indicator, calls LLM, and sends the reply.
    /// </summary>
    /// <param name="sender">Sender abstraction for composing and text messages.</param>
    /// <param name="message">The inbound message event from the Node bridge.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task HandleMessageAsync(IWhatsAppSender sender, NodeEvent message, CancellationToken ct)
    {
        // Validate message has required fields
        if (message.ChatId is null || message.Text is null)
        {
            _logger?.LogDebug("Message missing chatId or text, skipping");
            return;
        }

        var chatId = message.ChatId;

        // Dedup -- skip if we already processed this message ID
        if (message.Id is not null && !TryRecordMessage(message.Id))
        {
            _logger?.LogDebug("Duplicate message {MessageId} from chat {ChatId}, skipping", message.Id, chatId);
            return;
        }

        // Conversation gating -- check if conversation exists or if new ones are allowed
        var existing = _store.FindChannelConversation("whatsapp", _connectionId, chatId);
        if (existing is null)
        {
            var connection = _connectionStore.Load(_connectionId);
            if (connection is null || !connection.AllowNewConversations)
            {
                _logger?.LogInformation("New conversation from chat {ChatId} dropped — new conversations not allowed", chatId);
                return;
            }

            // Auto-lock: disable new conversations after the first one is created
            connection.AllowNewConversations = false;
            _connectionStore.Save(connection);
            _logger?.LogInformation("First conversation created for connection {ConnectionId}, auto-locked new conversations", _connectionId);
        }

        // Get or create conversation — new conversations use agent config, existing ones keep their provider/model
        var conversation = _store.FindOrCreateChannelConversation(
            "whatsapp", _connectionId, chatId,
            "whatsapp",
            _agentConfig.TextProvider, _agentConfig.TextModel,
            _agentConfig.VoiceProvider, _agentConfig.VoiceModel);

        // Mention filter — drop message silently if no mention matches
        if (!MentionMatcher.ShouldAccept(conversation, message.Text))
        {
            _logger?.LogDebug("Mention filter dropped message in conversation {ConversationId}", conversation.Id);
            return;
        }

        // Send composing indicator (best-effort)
        try
        {
            await sender.SendComposingAsync(chatId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to send composing indicator to chat {ChatId}", chatId);
        }

        _logger?.LogInformation("Message from chat {ChatId}: {Text}", chatId, message.Text);

        // Resolve provider from the conversation, not from agent config — existing conversations keep their provider
        var textProvider = _textProviderResolver(conversation.TextProvider);

        // For group messages, prefix user text with sender name
        var userText = message.Text;
        if (chatId.EndsWith("@g.us", StringComparison.Ordinal) && message.PushName is not null)
        {
            userText = $"[{message.PushName}] {userText}";
        }

        // Build user message. ReplyToChannelMessageId comes from Baileys
        // contextInfo.stanzaId — when set, the LLM-context builder will render the
        // replied-to message inline as a markdown blockquote.
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = userText,
            ChannelMessageId = message.Id,
            ReplyToChannelMessageId = message.ReplyTo
        };

        // Call LLM and collect response
        string replyText;
        string? assistantMessageId = null;
        try
        {
            var sb = new StringBuilder();
            var events = textProvider.CompleteAsync(conversation, userMessage, ct);
            await foreach (var evt in events.WithCancellation(ct))
            {
                switch (evt)
                {
                    case TextDelta delta:
                        sb.Append(delta.Content);
                        break;
                    case AssistantMessageSaved saved:
                        assistantMessageId = saved.MessageId;
                        break;
                }
            }

            replyText = sb.ToString();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LLM completion failed for chat {ChatId}", chatId);
            replyText = $"Something went wrong: {ex.Message}";
        }

        // Convert markdown to WhatsApp formatting
        var whatsAppText = WhatsAppMarkdownConverter.ToWhatsApp(replyText);

        // Chunk and send. Keep the first chunk's stanza ID — that's what users will reply to.
        var chunks = WhatsAppMarkdownConverter.ChunkText(whatsAppText, WhatsAppMaxMessageLength);
        string? firstStanzaId = null;
        foreach (var chunk in chunks)
        {
            var stanzaId = await sender.SendTextAsync(chatId, chunk);
            firstStanzaId ??= stanzaId;
        }

        // Back-fill the assistant message with the real Baileys stanza ID so reply-to-bot
        // lookups resolve correctly in BuildChatMessages / BuildMessages.
        if (assistantMessageId is not null && firstStanzaId is not null)
        {
            _store.UpdateChannelMessageId(assistantMessageId, firstStanzaId);
            _logger?.LogDebug("Updated assistant message {MessageId} for chat {ChatId} with stanza {StanzaId}",
                assistantMessageId, chatId, firstStanzaId);
        }

        _logger?.LogInformation("Reply sent to chat {ChatId}, {ChunkCount} chunk(s)", chatId, chunks.Count);
    }

    /// <summary>
    /// Records a message ID as processed. Returns true if the message is new,
    /// false if it was already seen (duplicate). Evicts expired entries when
    /// the cache exceeds the threshold.
    /// </summary>
    private bool TryRecordMessage(string messageId)
    {
        var now = DateTime.UtcNow;

        // Evict expired entries if we're above the threshold
        if (_processedMessages.Count > DedupEvictThreshold)
        {
            var expired = new List<string>();
            foreach (var (key, timestamp) in _processedMessages)
            {
                if (now - timestamp > DedupTtl)
                    expired.Add(key);
            }

            foreach (var key in expired)
                _processedMessages.Remove(key);

            // Hard cap: if still over max, clear the oldest half
            if (_processedMessages.Count >= DedupMaxEntries)
            {
                _logger?.LogWarning("Dedup cache hit hard cap ({Count}), clearing", _processedMessages.Count);
                _processedMessages.Clear();
            }
        }

        // Check if already seen (within TTL)
        if (_processedMessages.TryGetValue(messageId, out var existingTime))
        {
            if (now - existingTime <= DedupTtl)
                return false; // duplicate

            // Expired entry -- allow reprocessing
            _processedMessages[messageId] = now;
            return true;
        }

        _processedMessages[messageId] = now;
        return true;
    }
}
