using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Minimal IAgentLogic that forwards persistence calls to an <see cref="IConversationStore"/> and
/// lets tests configure Tools, tool execution, and CompactAsync behavior. Unlike
/// AgentLogicCompactAsyncTests's TestAgentLogic (which always reports zero tools), this fake
/// supports exercising a real tool-call round — needed by provider-level HTTP tests that must
/// prove message ordering across multiple LLM round-trips.
/// </summary>
public sealed class SimpleAgentLogic(IConversationStore store) : IAgentLogic
{
    /// <summary>Tools advertised to the provider. Empty by default (no tool-call round).</summary>
    public IReadOnlyList<AgentToolDefinition> Tools { get; set; } = [];

    /// <summary>Invoked for every tool call as (name, arguments) -&gt; JSON result. Defaults to "{}".</summary>
    public Func<string, string, Task<string>> ExecuteTool { get; set; } = (_, _) => Task.FromResult("{}");

    /// <summary>
    /// Overrides CompactAsync's result. Defaults to forwarding to the store (which for
    /// <see cref="InMemoryConversationStore"/> always returns false — no real summarizer).
    /// Tests exercising the overflow-retry path set this to simulate a successful compaction.
    /// </summary>
    public Func<string, CompactionReason, string?, CancellationToken, Task<bool>>? CompactOverride { get; set; }

    public string GetSystemPrompt(string conversationId, string source, bool voice, IReadOnlyList<string>? activeSkills = null, string? intention = null)
        => "test system prompt";

    public Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default)
        => ExecuteTool(name, arguments);

    public void AddMessage(string conversationId, Message message) => store.AddMessage(conversationId, message);

    public void DeleteMessages(string conversationId, IReadOnlyList<string> messageIds) => store.DeleteMessages(conversationId, messageIds);

    public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
        => store.GetMessages(conversationId, includeToolResultBlobs);

    public Conversation? GetConversation(string conversationId) => store.Get(conversationId);

    public void UpdateConversation(Conversation conversation) => store.Update(conversation);

    public void SetVoiceSession(string conversationId, string? sessionId, bool open) => store.SetVoiceSession(conversationId, sessionId, open);

    public void AddTokenUsage(string conversationId, int promptTokens, int completionTokens)
        => store.AddTokenUsage(conversationId, promptTokens, completionTokens);

    public Task<bool> CompactAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default)
        => CompactOverride is not null
            ? CompactOverride(conversationId, reason, customInstructions, ct)
            : store.CompactNowAsync(conversationId, reason, customInstructions, ct);
}
