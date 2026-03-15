using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
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

    public string GetSystemPrompt(string source, ConversationType type)
        // TODO: incorporate source to support channel-specific prompt variants (e.g., app vs telegram).
        => promptBuilder.Build(type);

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
        return await tool.ExecuteAsync(arguments, ct);
    }

    public void AddMessage(string conversationId, Message message)
        => store.AddMessage(conversationId, message);

    public IReadOnlyList<Message> GetMessages(string conversationId)
        => store.GetMessages(conversationId);

    public void UpdateConversation(Conversation conversation)
        => store.Update(conversation);
}
