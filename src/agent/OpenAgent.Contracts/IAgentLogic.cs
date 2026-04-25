using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

/// <summary>
/// Defines the agent's personality and capabilities — its system prompt, available tools,
/// and how tool calls are executed.
/// </summary>
public interface IAgentLogic
{
    /// <summary>Returns the system-level prompt tailored to the conversation id, source, type, active skills, and intention.</summary>
    string GetSystemPrompt(string conversationId, string source, ConversationType type, IReadOnlyList<string>? activeSkills = null, string? intention = null);

    /// <summary>Tools the agent can invoke during a conversation.</summary>
    IReadOnlyList<AgentToolDefinition> Tools { get; }

    /// <summary>Executes a tool by name and returns the JSON result.</summary>
    Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default);

    /// <summary>Persists a message in the conversation history.</summary>
    void AddMessage(string conversationId, Message message);

    /// <summary>
    /// Returns the full message history for a conversation. When <paramref name="includeToolResultBlobs"/>
    /// is true, tool result messages include their full on-disk content via
    /// <see cref="Message.FullToolResult"/>; providers should pass true when building LLM requests.
    /// </summary>
    IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false);

    /// <summary>Returns the conversation by ID, or null if not found.</summary>
    Conversation? GetConversation(string conversationId);

    /// <summary>Persists changes to the conversation (e.g. token counts).</summary>
    void UpdateConversation(Conversation conversation);

    /// <summary>
    /// Triggers compaction on-demand. Thin pass-through to
    /// <see cref="IConversationStore.CompactNowAsync"/>. Providers use this during overflow
    /// recovery to avoid reaching into the store directly.
    /// </summary>
    Task<bool> CompactAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default);
}

/// <summary>
/// Schema definition for a single tool that can be offered to an LLM (name, description, JSON Schema parameters).
/// </summary>
public sealed class AgentToolDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required object Parameters { get; init; } // JSON Schema object
}
