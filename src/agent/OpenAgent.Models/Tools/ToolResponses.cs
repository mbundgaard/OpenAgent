using System.Text.Json.Serialization;

namespace OpenAgent.Models.Tools;

/// <summary>
/// Tool definition returned by the tools API — name, description, and JSON Schema parameters.
/// </summary>
public sealed class ToolDefinitionResponse
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    public required object Parameters { get; init; }
}

/// <summary>
/// Result of executing a tool via the tools API.
/// </summary>
public sealed class ToolExecutionResponse
{
    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("duration_ms")]
    public required long DurationMs { get; init; }
}
