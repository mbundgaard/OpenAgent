using System.Text.Json.Serialization;

namespace OpenAgent.Models.Text;

/// <summary>
/// Final assistant text completion payload.
/// </summary>
public sealed class TextResponse
{
    /// <summary>Assistant message content.</summary>
    [JsonPropertyName("content")]
    public required string Content { get; init; }
    /// <summary>Assistant role label.</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }
}
