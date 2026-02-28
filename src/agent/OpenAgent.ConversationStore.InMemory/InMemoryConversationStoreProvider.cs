using System.Collections.Concurrent;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.ConversationStore.InMemory;

public sealed class InMemoryConversationStoreProvider : IConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } = [];

    public void Configure(JsonElement configuration)
    {
    }

    public Conversation Create()
    {
        var conversation = new Conversation { Id = Guid.NewGuid().ToString() };
        _conversations[conversation.Id] = conversation;
        return conversation;
    }

    public Conversation? Get(string id) => _conversations.GetValueOrDefault(id);

    public bool Delete(string id) => _conversations.TryRemove(id, out _);
}