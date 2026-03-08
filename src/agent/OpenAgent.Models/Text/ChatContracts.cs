using System.Text.Json.Serialization;

namespace OpenAgent.Models.Text;

/// <summary>
/// REST chat request payload.
/// </summary>
public sealed class ChatRequest
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

