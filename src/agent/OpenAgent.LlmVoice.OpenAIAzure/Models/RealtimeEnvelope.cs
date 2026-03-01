using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.LlmVoice.OpenAIAzure.Models;

/// <summary>
/// Deserialization target for incoming Azure OpenAI Realtime WebSocket messages.
/// A superset of all possible server event fields — only the relevant ones are populated per event type.
/// </summary>
internal sealed class RealtimeEnvelope
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
/// Serialization model for outgoing Azure OpenAI Realtime WebSocket messages.
/// </summary>
internal sealed class ClientEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealtimeSessionConfig? Session { get; set; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Audio { get; set; }

    [JsonPropertyName("item")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Item { get; set; }
}
