using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

/// <summary>
/// Persistence abstraction for managing conversation lifecycle.
/// </summary>
public interface IConversationStore : IConfigurable
{
    /// <summary>Creates a new conversation with a generated ID.</summary>
    Conversation Create();

    /// <summary>Returns the conversation with the given ID, or null if not found.</summary>
    Conversation? Get(string id);

    /// <summary>Persists changes to an existing conversation.</summary>
    void Update(Conversation conversation);

    /// <summary>Removes the conversation. Returns true if it existed.</summary>
    bool Delete(string id);

    /// <summary>Persists a message in the given conversation.</summary>
    void AddMessage(string conversationId, Message message);

    /// <summary>Returns all messages for the given conversation, in order.</summary>
    IReadOnlyList<Message> GetMessages(string conversationId);
}
