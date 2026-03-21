using System.Text.Json.Serialization;

namespace OpenAgent.Models.Common;

/// <summary>
/// Optional settings for raw LLM completions (without a conversation context).
/// </summary>
public sealed record CompletionOptions
{
    /// <summary>Response format hint, e.g. "json_object" for structured output.</summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; init; }
}
