using System.Text.Json.Serialization;

namespace OpenAgent.Models.Voice;

/// <summary>
/// Base payload for outbound voice WebSocket events.
/// </summary>
public class VoiceWebSocketEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

/// <summary>
/// Outbound voice transcript payload.
/// </summary>
public sealed class VoiceTranscriptEvent : VoiceWebSocketEvent
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

/// <summary>
/// Outbound voice error payload.
/// </summary>
public sealed class VoiceErrorEvent : VoiceWebSocketEvent
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
