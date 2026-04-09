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
            ElapsedMs = message.ElapsedMs
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
}
