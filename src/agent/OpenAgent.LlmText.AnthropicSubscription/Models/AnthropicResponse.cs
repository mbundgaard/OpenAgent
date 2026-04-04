using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.AnthropicSubscription.Models;

/// <summary>
/// Wraps a single SSE event from the Anthropic streaming API.
/// The event type determines which fields are populated.
/// </summary>
internal sealed class AnthropicStreamEvent
{
    public required string EventType { get; set; }
    public JsonElement Data { get; set; }
}

// message_start: { "type": "message_start", "message": { ... } }
internal sealed class MessageStartEvent
{
    [JsonPropertyName("message")]
    public AnthropicResponseMessage? Message { get; set; }
}

internal sealed class AnthropicResponseMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

internal sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

// content_block_start
internal sealed class ContentBlockStartEvent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("content_block")]
    public ContentBlockStub? ContentBlock { get; set; }
}

internal sealed class ContentBlockStub
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

// content_block_delta
internal sealed class ContentBlockDeltaEvent
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public DeltaPayload? Delta { get; set; }
}

internal sealed class DeltaPayload
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    // text_delta
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    // input_json_delta
    [JsonPropertyName("partial_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartialJson { get; set; }
}

// message_delta
internal sealed class MessageDeltaEvent
{
    [JsonPropertyName("delta")]
    public MessageDeltaPayload? Delta { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

internal sealed class MessageDeltaPayload
{
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }
}
