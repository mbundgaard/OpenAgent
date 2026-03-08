using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent;

/// <summary>
/// Core agent logic. Composes system prompts from markdown files and delegates
/// message persistence to the conversation store.
/// </summary>
internal sealed class AgentLogic(IConversationStore store, SystemPromptBuilder promptBuilder) : IAgentLogic
{
    public string GetSystemPrompt(string source, ConversationType type)
        // TODO: incorporate source to support channel-specific prompt variants (e.g., app vs telegram).
        => promptBuilder.Build(type);

    public IReadOnlyList<AgentToolDefinition> Tools => [];

    public Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default)
        => Task.FromResult("{}");

    public void AddMessage(string conversationId, Message message)
        => store.AddMessage(conversationId, message);

    public IReadOnlyList<Message> GetMessages(string conversationId)
        => store.GetMessages(conversationId);
}
