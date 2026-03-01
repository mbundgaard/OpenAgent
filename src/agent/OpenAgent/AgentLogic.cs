using OpenAgent.Contracts;

namespace OpenAgent;

internal sealed class AgentLogic : IAgentLogic
{
    public string SystemPrompt => "";

    public IReadOnlyList<AgentToolDefinition> Tools => [];

    public Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default) => Task.FromResult("{}");
}
