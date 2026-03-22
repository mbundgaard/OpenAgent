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

    /// <summary>
    /// The channel message ID this message is replying to (e.g. Telegram reply_to_message ID).
    /// Null when the message is not a reply to a specific message.
    /// </summary>
    [JsonPropertyName("reply_to_channel_message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplyToChannelMessageId { get; init; }

    /// <summary>
    /// SQLite rowid — populated by the store, not persisted via INSERT.
    /// Used for compaction boundary tracking.
    /// </summary>
    [JsonPropertyName("row_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long RowId { get; init; }

    /// <summary>Prompt tokens used for this turn (assistant messages only).</summary>
    [JsonPropertyName("prompt_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PromptTokens { get; init; }

    /// <summary>Completion tokens generated for this turn (assistant messages only).</summary>
    [JsonPropertyName("completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletionTokens { get; init; }

    /// <summary>Elapsed time in milliseconds for this turn (assistant messages only).</summary>
    [JsonPropertyName("elapsed_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ElapsedMs { get; init; }
}
