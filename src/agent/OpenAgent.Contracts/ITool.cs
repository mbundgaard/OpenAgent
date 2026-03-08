namespace OpenAgent.Contracts;

/// <summary>
/// A single tool the agent can invoke — carries its own definition and execution logic.
/// </summary>
public interface ITool
{
    /// <summary>Schema definition sent to the LLM (name, description, parameters).</summary>
    AgentToolDefinition Definition { get; }

    /// <summary>Executes the tool with JSON arguments and returns a JSON result.</summary>
    Task<string> ExecuteAsync(string arguments, CancellationToken ct = default);
}
