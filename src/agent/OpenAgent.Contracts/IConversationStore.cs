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

    /// <summary>Returns all messages for the given conversation, in order.</summary>
    IReadOnlyList<Message> GetMessages(string conversationId);

    /// <summary>Returns messages by their IDs, regardless of compaction state. Used by the expand tool.</summary>
    IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds);

    /// <summary>
    /// Purges old tool rounds from a conversation. Within a single transaction:
    /// assistant rows with ToolCalls and their matching tool-result children are nulled out together,
    /// but only if the assistant row falls outside the last <paramref name="keepLast"/> such rows
    /// AND its CreatedAt is older than <paramref name="cutoff"/>. Returns (roundsPurged, resultRowsPurged).
    /// See docs/plans/2026-04-19-context-pruning-design.md.
    /// </summary>
    (int RoundsPurged, int ResultRowsPurged) PurgeOldToolRounds(
        string conversationId, int keepLast, DateTimeOffset cutoff);

    /// <summary>
    /// Purges all unpurged activate_skill_resource results in this conversation, regardless of age
    /// or recency. Called by DeactivateSkillTool so resources don't linger when the user declares
    /// a skill is done. Returns the number of rows nulled.
    /// </summary>
    int PurgeSkillResourceResults(string conversationId);
}
