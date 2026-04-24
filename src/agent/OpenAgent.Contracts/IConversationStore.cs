using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

/// <summary>
/// Persistence abstraction for managing conversation lifecycle.
/// </summary>
public interface IConversationStore : IConfigurable
{
    /// <summary>Returns the existing conversation or creates a new one stamped with provider and model.</summary>
    Conversation GetOrCreate(string conversationId, string source, ConversationType type, string provider, string model);

    /// <summary>
    /// Looks up a conversation bound to a specific external chat, or returns null if not found.
    /// Used by channel handlers for gating checks before deciding whether to create a new conversation.
    /// </summary>
    Conversation? FindChannelConversation(string channelType, string connectionId, string channelChatId);

    /// <summary>
    /// Finds a conversation bound to a specific external chat, or creates a new one with a fresh GUID
    /// and the channel binding fields set. Used by channel handlers to resolve inbound messages to
    /// conversations without derived ID strings.
    /// </summary>
    Conversation FindOrCreateChannelConversation(
        string channelType,
        string connectionId,
        string channelChatId,
        string source,
        ConversationType type,
        string provider,
        string model);

    /// <summary>Returns all conversations.</summary>
    IReadOnlyList<Conversation> GetAll();

    /// <summary>Returns the conversation with the given ID, or null if not found.</summary>
    Conversation? Get(string conversationId);

    /// <summary>Persists changes to an existing conversation.</summary>
    void Update(Conversation conversation);

    /// <summary>Updates the conversation's Type. No-op if the conversation does not exist or already has this type.</summary>
    void UpdateType(string conversationId, ConversationType type);

    /// <summary>
    /// Updates the conversation's human-readable display name. No-op if the value is unchanged.
    /// Channel providers call this on every inbound message so renames propagate.
    /// </summary>
    void UpdateDisplayName(string conversationId, string? displayName);

    /// <summary>Removes the conversation. Returns true if it existed.</summary>
    bool Delete(string conversationId);

    /// <summary>Persists a message in the given conversation.</summary>
    void AddMessage(string conversationId, Message message);

    /// <summary>Updates the channel message ID on an existing message.</summary>
    void UpdateChannelMessageId(string messageId, string channelMessageId);

    /// <summary>
    /// Returns all messages for the given conversation, in order.
    /// When <paramref name="includeToolResultBlobs"/> is true, tool result messages have their
    /// <see cref="Message.FullToolResult"/> populated by reading the on-disk blob referenced by
    /// <see cref="Message.ToolResultRef"/>. If the blob is missing, FullToolResult stays null and
    /// the caller falls back to <see cref="Message.Content"/>.
    /// </summary>
    IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false);

    /// <summary>Returns messages by their IDs, regardless of compaction state. Used by the expand tool.</summary>
    IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds);
}
