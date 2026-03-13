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

    // IConfigurable — no-op for tests
    public string Key => "";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }

    public Conversation GetOrCreate(string conversationId, string source, ConversationType type)
    {
        if (_conversations.TryGetValue(conversationId, out var existing))
            return existing;

        var conversation = new Conversation
        {
            Id = conversationId,
            Source = source,
            Type = type
        };

        _conversations[conversationId] = conversation;
        _messages[conversationId] = [];
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
        _messages[conversationId].Add(message);
    }

    public IReadOnlyList<Message> GetMessages(string conversationId) =>
        _messages.GetValueOrDefault(conversationId)?.AsReadOnly()
        ?? (IReadOnlyList<Message>)Array.Empty<Message>();
}
