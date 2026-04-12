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

/// <summary>
/// Outbound session-ready payload. Emitted once per session before audio flows.
/// Client uses these values to configure AudioContext and microphone capture.
/// </summary>
public sealed class VoiceSessionReadyEvent : VoiceWebSocketEvent
{
    [JsonPropertyName("input_sample_rate")]
    public required int InputSampleRate { get; init; }

    [JsonPropertyName("output_sample_rate")]
    public required int OutputSampleRate { get; init; }

    [JsonPropertyName("input_codec")]
    public required string InputCodec { get; init; }

    [JsonPropertyName("output_codec")]
    public required string OutputCodec { get; init; }
}
