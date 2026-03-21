using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.OpenAIAzure.Models;

public sealed class ResponseFormatSpec
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}
