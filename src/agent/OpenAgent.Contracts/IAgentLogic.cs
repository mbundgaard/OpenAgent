namespace OpenAgent.Contracts;

public interface IAgentLogic
{
    string SystemPrompt { get; }
    IReadOnlyList<AgentToolDefinition> Tools { get; }
    Task<string> ExecuteToolAsync(string name, string arguments, CancellationToken ct = default);
}

public sealed class AgentToolDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required object Parameters { get; init; } // JSON Schema object
}
