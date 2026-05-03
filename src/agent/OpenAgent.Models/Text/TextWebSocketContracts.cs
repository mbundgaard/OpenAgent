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
/// Outbound text WebSocket delta payload — a chunk of streamed text.
/// </summary>
public sealed class TextWebSocketDelta
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "delta";

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

/// <summary>
/// Outbound text WebSocket tool call payload — the LLM is invoking a tool.
/// </summary>
public sealed class TextWebSocketToolCall
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_call";

    [JsonPropertyName("tool_call_id")]
    public required string ToolCallId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; init; }
}

/// <summary>
/// Outbound text WebSocket tool result payload — a tool finished executing.
/// </summary>
public sealed class TextWebSocketToolResult
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_result";

    [JsonPropertyName("tool_call_id")]
    public required string ToolCallId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }
}

/// <summary>
/// Outbound text WebSocket done payload — completion finished.
/// </summary>
public sealed class TextWebSocketDone
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "done";
}

/// <summary>
/// Outbound text WebSocket status payload — tool execution is starting for this turn.
/// </summary>
public sealed class TextWebSocketToolCallStarted
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_call_started";
}

/// <summary>
/// Outbound text WebSocket status payload — all tool execution is done for this turn.
/// </summary>
public sealed class TextWebSocketToolCallCompleted
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_call_completed";
}
