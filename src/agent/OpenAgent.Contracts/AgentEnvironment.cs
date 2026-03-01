namespace OpenAgent.Contracts;

/// <summary>
/// Shared environment settings resolved once at startup and injected into all components.
/// </summary>
public sealed class AgentEnvironment
{
    /// <summary>Root directory for all persistent data (config, database, logs).</summary>
    public required string DataPath { get; init; }
}
