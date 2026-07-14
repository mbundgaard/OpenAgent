using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;

namespace OpenAgent;

/// <summary>
/// Core agent logic. Composes system prompts from markdown files, aggregates tools
/// from all registered handlers, and delegates message persistence to the conversation store.
/// </summary>
internal sealed class AgentLogic(
    IConversationStore store,
    SystemPromptBuilder promptBuilder,
    IEnumerable<IToolHandler> toolHandlers,
    ILogger<AgentLogic> logger) : IAgentLogic
{
    // Flatten all tools from all handlers into a single list
    private readonly IReadOnlyList<ITool> _allTools = toolHandlers.SelectMany(h => h.Tools).ToList();

    public string GetSystemPrompt(string conversationId, string source, bool voice, IReadOnlyList<string>? activeSkills = null, string? intention = null)
        => promptBuilder.Build(conversationId, voice, activeSkills, intention, source);

    public IReadOnlyList<AgentToolDefinition> Tools =>
        _allTools.Select(t => t.Definition).ToList();

    public async Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default)
    {
        // Find the tool by name across all handlers
        var tool = _allTools.FirstOrDefault(t => t.Definition.Name == name);
        if (tool is null)
        {
            logger.LogWarning("Tool {ToolName} not found, conversation {ConversationId}", name, conversationId);
            return """{"error": "tool not found"}""";
        }

        logger.LogDebug("Executing tool {ToolName} for conversation {ConversationId}", name, conversationId);
        var result = await tool.ExecuteAsync(arguments, conversationId, ct);

        // Cap oversized output before it enters history / the LLM context. An unbounded
        // tool result can exceed the context window and wedge the conversation beyond
        // compaction's reach, so truncate with a notice that tells the agent to narrow.
        if (result is { Length: > ToolOutputCap.DefaultMaxChars })
            logger.LogWarning("Tool {ToolName} output {Length} chars exceeds cap {Cap}, truncating; conversation {ConversationId}",
                name, result.Length, ToolOutputCap.DefaultMaxChars, conversationId);

        return ToolOutputCap.Apply(result);
    }

    public void AddMessage(string conversationId, Message message)
        => store.AddMessage(conversationId, message);

    public void DeleteMessages(string conversationId, IReadOnlyList<string> messageIds)
        => store.DeleteMessages(conversationId, messageIds);

    public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
        => store.GetMessages(conversationId, includeToolResultBlobs);

    public Conversation? GetConversation(string conversationId)
        => store.Get(conversationId);

    public void UpdateConversation(Conversation conversation)
        => store.Update(conversation);

    public void SetVoiceSession(string conversationId, string? sessionId, bool open)
        => store.SetVoiceSession(conversationId, sessionId, open);

    public void AddTokenUsage(string conversationId, int promptTokens, int completionTokens)
        => store.AddTokenUsage(conversationId, promptTokens, completionTokens);

    public Task<bool> CompactAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default)
        => store.CompactNowAsync(conversationId, reason, customInstructions, ct);
}
