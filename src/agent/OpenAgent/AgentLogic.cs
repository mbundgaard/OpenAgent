using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent;

/// <summary>
/// Default no-op agent implementation. Returns an empty system prompt and no tools.
/// Placeholder until a real agent is configured.
/// </summary>
internal sealed class AgentLogic(IConversationStore store) : IAgentLogic
{
    public string GetSystemPrompt(string source, ConversationType type) => "";

    public IReadOnlyList<AgentToolDefinition> Tools => [];

    public Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default)
        => Task.FromResult("{}");

    public void AddMessage(string conversationId, Message message)
        => store.AddMessage(conversationId, message);

    public IReadOnlyList<Message> GetMessages(string conversationId)
        => store.GetMessages(conversationId);
}
