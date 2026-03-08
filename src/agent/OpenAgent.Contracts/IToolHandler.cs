namespace OpenAgent.Contracts;

/// <summary>
/// Groups related tools under a single capability domain (e.g., filesystem, web, database).
/// Registered in DI so AgentLogic can aggregate all tools across handlers.
/// </summary>
public interface IToolHandler
{
    /// <summary>All tools this handler provides.</summary>
    IReadOnlyList<ITool> Tools { get; }
}
