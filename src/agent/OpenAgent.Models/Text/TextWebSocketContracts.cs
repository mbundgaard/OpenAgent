using System.Text.Json.Serialization;

namespace OpenAgent.Models.Text;

/// <summary>
/// Inbound text WebSocket chat payload.
/// </summary>
public sealed class TextWebSocketInboundMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

/// <summary>
/// Outbound text WebSocket delta payload.
/// </summary>
public sealed class TextWebSocketDelta
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "delta";

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

/// <summary>
/// Outbound text WebSocket done payload.
/// </summary>
public sealed class TextWebSocketDone
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "done";
}
