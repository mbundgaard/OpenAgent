using System.Collections.Concurrent;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.ConversationStore.InMemory;

/// <summary>
/// In-memory conversation store backed by a ConcurrentDictionary. Data does not survive restarts.
/// Intended for development and testing.
/// </summary>
public sealed class InMemoryConversationStoreProvider : IConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();
    private readonly ConcurrentDictionary<string, List<Message>> _messages = new();

    public string Key => "conversation-store";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } = [];

    public void Configure(JsonElement configuration)
    {
    }

    public Conversation GetOrCreate(string conversationId, string source, ConversationType type)
    {
        return _conversations.GetOrAdd(conversationId,
            _ => new Conversation { Id = conversationId, Source = source, Type = type });
    }

    public IReadOnlyList<Conversation> GetAll() => _conversations.Values.ToList();

    public Conversation? Get(string conversationId) => _conversations.GetValueOrDefault(conversationId);

    public void Update(Conversation conversation) => _conversations[conversation.Id] = conversation;

    public bool Delete(string conversationId)
    {
        _messages.TryRemove(conversationId, out _);
        return _conversations.TryRemove(conversationId, out _);
    }

    public void AddMessage(string conversationId, Message message)
    {
        var list = _messages.GetOrAdd(conversationId, _ => []);
        lock (list) { list.Add(message); }
    }

    public IReadOnlyList<Message> GetMessages(string conversationId)
    {
        if (!_messages.TryGetValue(conversationId, out var list))
            return [];
        lock (list) { return list.ToList(); }
    }
}