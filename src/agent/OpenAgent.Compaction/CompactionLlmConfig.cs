namespace OpenAgent.Compaction;

/// <summary>
/// LLM configuration for the compaction summarizer.
/// Can point to a different deployment/model than the main text provider (e.g. a cheaper model).
/// </summary>
public sealed class CompactionLlmConfig
{
    public required string ApiKey { get; init; }
    public required string Endpoint { get; init; }
    public required string DeploymentName { get; init; }
    public string ApiVersion { get; init; } = "2025-04-01-preview";
}
