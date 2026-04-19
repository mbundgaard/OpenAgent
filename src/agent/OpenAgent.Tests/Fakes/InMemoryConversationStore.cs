using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Dictionary-based in-memory conversation store for unit tests.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<string, Conversation> _conversations = new();
    private readonly Dictionary<string, List<Message>> _messages = new();
    private long _nextRowId = 1;

    // IConfigurable — no-op for tests
    public string Key => "";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }

    public Conversation GetOrCreate(string conversationId, string source, ConversationType type, string provider, string model)
    {
        if (_conversations.TryGetValue(conversationId, out var existing))
            return existing;

        var conversation = new Conversation
        {
            Id = conversationId,
            Source = source,
            Type = type,
            Provider = provider,
            Model = model
        };

        _conversations[conversationId] = conversation;
        _messages[conversationId] = [];
        return conversation;
    }

    public Conversation? FindChannelConversation(string channelType, string connectionId, string channelChatId) =>
        _conversations.Values.FirstOrDefault(c =>
            c.ChannelType == channelType &&
            c.ConnectionId == connectionId &&
            c.ChannelChatId == channelChatId);

    public Conversation FindOrCreateChannelConversation(
        string channelType,
        string connectionId,
        string channelChatId,
        string source,
        ConversationType type,
        string provider,
        string model)
    {
        var existing = FindChannelConversation(channelType, connectionId, channelChatId);
        if (existing is not null)
            return existing;

        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString(),
            Source = source,
            Type = type,
            Provider = provider,
            Model = model,
            ChannelType = channelType,
            ConnectionId = connectionId,
            ChannelChatId = channelChatId
        };

        _conversations[conversation.Id] = conversation;
        _messages[conversation.Id] = [];
        return conversation;
    }

    public IReadOnlyList<Conversation> GetAll() => _conversations.Values.ToList();

    public Conversation? Get(string conversationId) =>
        _conversations.GetValueOrDefault(conversationId);

    public void Update(Conversation conversation) =>
        _conversations[conversation.Id] = conversation;

    public void UpdateType(string conversationId, ConversationType type)
    {
        if (_conversations.TryGetValue(conversationId, out var conv) && conv.Type != type)
            conv.Type = type;
    }

    public void UpdateDisplayName(string conversationId, string? displayName)
    {
        if (_conversations.TryGetValue(conversationId, out var conv) && conv.DisplayName != displayName)
            conv.DisplayName = displayName;
    }

    public bool Delete(string conversationId) =>
        _conversations.Remove(conversationId) | _messages.Remove(conversationId);

    public void AddMessage(string conversationId, Message message)
    {
        if (!_messages.ContainsKey(conversationId))
            _messages[conversationId] = [];

        var withRowId = new Message
        {
            RowId = _nextRowId++,
            Id = message.Id,
            ConversationId = message.ConversationId,
            Role = message.Role,
            Content = message.Content,
            CreatedAt = message.CreatedAt,
            ToolCalls = message.ToolCalls,
            ToolCallId = message.ToolCallId,
            ChannelMessageId = message.ChannelMessageId,
            ReplyToChannelMessageId = message.ReplyToChannelMessageId,
            PromptTokens = message.PromptTokens,
            CompletionTokens = message.CompletionTokens,
            ElapsedMs = message.ElapsedMs,
            ToolType = message.ToolType,
            ToolResultPurgedAt = message.ToolResultPurgedAt,
            ToolCallsPurgedAt = message.ToolCallsPurgedAt
        };
        _messages[conversationId].Add(withRowId);
    }

    public void UpdateChannelMessageId(string messageId, string channelMessageId)
    {
        foreach (var messages in _messages.Values)
        {
            var idx = messages.FindIndex(m => m.Id == messageId);
            if (idx < 0) continue;

            // Message is immutable (init properties), so replace with a copy
            var old = messages[idx];
            messages[idx] = new Message
            {
                RowId = old.RowId,
                Id = old.Id,
                ConversationId = old.ConversationId,
                Role = old.Role,
                Content = old.Content,
                CreatedAt = old.CreatedAt,
                ToolCalls = old.ToolCalls,
                ToolCallId = old.ToolCallId,
                ChannelMessageId = channelMessageId,
                ReplyToChannelMessageId = old.ReplyToChannelMessageId
            };
            return;
        }
    }

    public IReadOnlyList<Message> GetMessages(string conversationId)
    {
        var conversation = Get(conversationId);
        var list = new List<Message>();

        if (conversation?.Context is not null)
        {
            list.Add(new Message
            {
                Id = "context",
                ConversationId = conversationId,
                Role = "system",
                Content = conversation.Context
            });
        }

        var allMessages = _messages.GetValueOrDefault(conversationId) ?? [];

        var messages = conversation?.CompactedUpToRowId is not null
            ? allMessages.Where(m => m.RowId > conversation.CompactedUpToRowId.Value).ToList()
            : allMessages;

        list.AddRange(messages);
        return list.AsReadOnly();
    }

    public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds)
    {
        var idSet = messageIds.ToHashSet();
        return _messages.Values
            .SelectMany(msgs => msgs)
            .Where(m => idSet.Contains(m.Id))
            .ToList();
    }

    public (int RoundsPurged, int ResultRowsPurged) PurgeOldToolRounds(
        string conversationId, int keepLast, DateTimeOffset cutoff)
    {
        if (!_messages.TryGetValue(conversationId, out var messages)) return (0, 0);

        var assistantRounds = messages
            .Select((m, idx) => (Message: m, Index: idx))
            .Where(x => x.Message.ToolCalls is not null && x.Message.ToolCallsPurgedAt is null)
            .OrderByDescending(x => x.Message.RowId)
            .Skip(keepLast)
            .Where(x => x.Message.CreatedAt < cutoff)
            .ToList();

        if (assistantRounds.Count == 0) return (0, 0);

        var toolCallIds = new HashSet<string>();
        foreach (var (msg, _) in assistantRounds)
        {
            try
            {
                using var doc = JsonDocument.Parse(msg.ToolCalls!);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var call in doc.RootElement.EnumerateArray())
                    if (call.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        toolCallIds.Add(idProp.GetString()!);
            }
            catch (JsonException) { }
        }

        var purgedAt = DateTimeOffset.UtcNow;
        var roundsPurged = 0;
        var resultRowsPurged = 0;

        foreach (var (msg, idx) in assistantRounds)
        {
            messages[idx] = CloneWithPurgedArgs(msg, purgedAt);
            roundsPurged++;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.ToolCallId is null || m.ToolResultPurgedAt is not null) continue;
            if (!toolCallIds.Contains(m.ToolCallId)) continue;
            messages[i] = CloneWithPurgedResult(m, purgedAt);
            resultRowsPurged++;
        }

        return (roundsPurged, resultRowsPurged);
    }

    public int PurgeSkillResourceResults(string conversationId)
    {
        if (!_messages.TryGetValue(conversationId, out var messages)) return 0;

        var purgedAt = DateTimeOffset.UtcNow;
        var purged = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.ToolType != "activate_skill_resource" || m.ToolResultPurgedAt is not null) continue;
            messages[i] = CloneWithPurgedResult(m, purgedAt);
            purged++;
        }
        return purged;
    }

    private static Message CloneWithPurgedArgs(Message m, DateTimeOffset purgedAt) =>
        new()
        {
            RowId = m.RowId, Id = m.Id, ConversationId = m.ConversationId, Role = m.Role,
            Content = m.Content, CreatedAt = m.CreatedAt,
            ToolCalls = null, // nulled
            ToolCallId = m.ToolCallId, ChannelMessageId = m.ChannelMessageId,
            ReplyToChannelMessageId = m.ReplyToChannelMessageId,
            PromptTokens = m.PromptTokens, CompletionTokens = m.CompletionTokens, ElapsedMs = m.ElapsedMs,
            Modality = m.Modality, ToolType = m.ToolType,
            ToolResultPurgedAt = m.ToolResultPurgedAt,
            ToolCallsPurgedAt = purgedAt // stamped
        };

    private static Message CloneWithPurgedResult(Message m, DateTimeOffset purgedAt) =>
        new()
        {
            RowId = m.RowId, Id = m.Id, ConversationId = m.ConversationId, Role = m.Role,
            Content = null, // nulled
            CreatedAt = m.CreatedAt,
            ToolCalls = m.ToolCalls,
            ToolCallId = m.ToolCallId, ChannelMessageId = m.ChannelMessageId,
            ReplyToChannelMessageId = m.ReplyToChannelMessageId,
            PromptTokens = m.PromptTokens, CompletionTokens = m.CompletionTokens, ElapsedMs = m.ElapsedMs,
            Modality = m.Modality, ToolType = m.ToolType,
            ToolResultPurgedAt = purgedAt, // stamped
            ToolCallsPurgedAt = m.ToolCallsPurgedAt
        };
}
