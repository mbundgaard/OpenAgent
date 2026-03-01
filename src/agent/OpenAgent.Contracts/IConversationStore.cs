using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

/// <summary>
/// Persistence abstraction for managing conversation lifecycle.
/// </summary>
public interface IConversationStore : IConfigurable
{
    /// <summary>Returns the existing conversation or creates a new one with the given ID.</summary>
    Conversation GetOrCreate(string conversationId, string source, ConversationType type);

    /// <summary>Returns the conversation with the given ID, or null if not found.</summary>
    Conversation? Get(string conversationId);

    /// <summary>Persists changes to an existing conversation.</summary>
    void Update(Conversation conversation);

    /// <summary>Removes the conversation. Returns true if it existed.</summary>
    bool Delete(string conversationId);

    /// <summary>Persists a message in the given conversation.</summary>
    void AddMessage(string conversationId, Message message);

    /// <summary>Returns all messages for the given conversation, in order.</summary>
    IReadOnlyList<Message> GetMessages(string conversationId);
}
