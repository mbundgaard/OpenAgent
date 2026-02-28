using System.Collections.Concurrent;
using OpenAgent.Contracts;
using OpenAgent.Models;

namespace ConversationStore.InMemory;

public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();

    public Conversation Create()
    {
        var conversation = new Conversation { Id = Guid.NewGuid().ToString() };
        _conversations[conversation.Id] = conversation;
        return conversation;
    }

    public Conversation? Get(string id) =>
        _conversations.GetValueOrDefault(id);

    public bool Delete(string id) =>
        _conversations.TryRemove(id, out _);
}
