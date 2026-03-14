using System.Text.Json.Serialization;

namespace OpenAgent.Models.Conversations;

/// <summary>
/// A single message within a conversation (user or assistant), with role, content, and timestamp.
/// </summary>
public sealed class Message
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; init; }
    [JsonPropertyName("role")]
    public required string Role { get; init; }
    [JsonPropertyName("content")]
    public string? Content { get; init; }
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Serialized JSON array of tool calls (only for assistant messages that invoke tools).
    /// </summary>
    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCalls { get; init; }

    /// <summary>
    /// The tool call ID this message is a response to (only for role "tool").
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>
    /// The message ID from the originating channel (e.g. Telegram message ID).
    /// Null for messages that don't originate from a channel.
    /// </summary>
    [JsonPropertyName("channel_message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelMessageId { get; init; }
}
