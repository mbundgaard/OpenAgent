using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.LlmVoice.GrokRealtime.Models;

/// <summary>
/// Superset DTO for incoming Grok Realtime WebSocket messages.
/// Wire format follows the OpenAI Realtime spec; only relevant fields are populated per event type.
/// </summary>
internal sealed class GrokEnvelope
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("session")]
    public JsonElement? Session { get; set; }

    [JsonPropertyName("delta")]
    public string? Delta { get; set; }

    [JsonPropertyName("transcript")]
    public string? Transcript { get; set; }

    [JsonPropertyName("audio")]
    public string? Audio { get; set; }

    [JsonPropertyName("error")]
    public JsonElement? Error { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    [JsonPropertyName("output_index")]
    public int? OutputIndex { get; set; }

    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }
}

/// <summary>
/// Outgoing Grok Realtime WebSocket message.
/// </summary>
internal sealed class GrokClientEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GrokSessionConfig? Session { get; set; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Audio { get; set; }

    [JsonPropertyName("item")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Item { get; set; }
}

/// <summary>
/// Session configuration sent in the session.update event.
/// </summary>
internal sealed class GrokSessionConfig
{
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("modalities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Modalities { get; set; }

    [JsonPropertyName("voice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Voice { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("input_audio_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputAudioFormat { get; set; }

    [JsonPropertyName("output_audio_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputAudioFormat { get; set; }

    [JsonPropertyName("input_audio_transcription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GrokTranscriptionConfig? InputAudioTranscription { get; set; }

    [JsonPropertyName("turn_detection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GrokTurnDetectionConfig? TurnDetection { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GrokToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; set; }
}

internal sealed class GrokTranscriptionConfig
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

internal sealed class GrokTurnDetectionConfig
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

internal sealed class GrokToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public required object Parameters { get; set; }
}
