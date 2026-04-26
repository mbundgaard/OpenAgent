using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Wire-level JSON envelope for Telnyx Media Streaming events. Handles parse and compose for the
/// minimal set used by the bridge: start/media/stop/dtmf inbound, media/clear outbound.
/// </summary>
public sealed class TelnyxMediaFrame
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("sequence_number")] public string? SequenceNumber { get; init; }
    [JsonPropertyName("stream_id")] public string? StreamId { get; init; }
    [JsonPropertyName("start")] public StartPayload? Start { get; init; }
    [JsonPropertyName("media")] public MediaPayload? Media { get; init; }
    [JsonPropertyName("stop")] public StopPayload? Stop { get; init; }
    [JsonPropertyName("dtmf")] public DtmfPayload? Dtmf { get; init; }

    public static TelnyxMediaFrame Parse(string json) =>
        JsonSerializer.Deserialize<TelnyxMediaFrame>(json, Options)
        ?? throw new JsonException("Empty or invalid Telnyx media frame.");

    public static string ComposeMedia(ReadOnlySpan<byte> audio)
    {
        var payload = Convert.ToBase64String(audio);
        return JsonSerializer.Serialize(new
        {
            @event = "media",
            media = new { payload }
        }, Options);
    }

    public static string ComposeClear() => """{"event":"clear"}""";

    public sealed class StartPayload
    {
        [JsonPropertyName("call_control_id")] public string? CallControlId { get; init; }
        [JsonPropertyName("client_state")] public string? ClientState { get; init; }
        [JsonPropertyName("media_format")] public MediaFormat MediaFormat { get; init; } = new();
    }

    public sealed class MediaFormat
    {
        [JsonPropertyName("encoding")] public string Encoding { get; init; } = "";
        [JsonPropertyName("sample_rate")] public int SampleRate { get; init; }
        [JsonPropertyName("channels")] public int Channels { get; init; }
    }

    public sealed class MediaPayload
    {
        [JsonPropertyName("track")] public string Track { get; init; } = "";
        [JsonPropertyName("chunk")] public string? Chunk { get; init; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }
        [JsonPropertyName("payload")] public string Payload { get; init; } = "";

        [JsonIgnore] public byte[] PayloadBytes => Convert.FromBase64String(Payload);
    }

    public sealed class StopPayload
    {
        [JsonPropertyName("reason")] public string? Reason { get; init; }
    }

    public sealed class DtmfPayload
    {
        [JsonPropertyName("digit")] public string Digit { get; init; } = "";
    }
}
