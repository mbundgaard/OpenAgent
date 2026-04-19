using System.Text.Json.Serialization;

namespace OpenAgent.Models.Conversations;

public enum MessageModality
{
    Text,
    Voice
}

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
    /// Whether this message originated as text input or as a voice transcript.
    /// Defaults to Text — voice provider sites set Voice explicitly.
    /// </summary>
    [JsonPropertyName("modality")]
    [JsonConverter(typeof(JsonStringEnumConverter<MessageModality>))]
    public MessageModality Modality { get; init; } = MessageModality.Text;

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

    /// <summary>
    /// The tool name (e.g. "file_read") this row's Content is a result of. Set only on tool-role
    /// rows — NULL for user/assistant/system rows and for pre-pruning-feature rows. Enables the
    /// round-based purge job to identify and rank tool-result rows without parsing Content JSON.
    /// </summary>
    [JsonPropertyName("tool_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolType { get; init; }

    /// <summary>
    /// Timestamp when this row's Content was nulled by the purge job (tool-role rows only).
    /// NULL = still live. Stored in the same format as CreatedAt (DateTimeOffset "O").
    /// </summary>
    [JsonPropertyName("tool_result_purged_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ToolResultPurgedAt { get; init; }

    /// <summary>
    /// Timestamp when this row's ToolCalls JSON was nulled by the purge job (assistant rows only).
    /// NULL = still live. Stored in the same format as CreatedAt.
    /// </summary>
    [JsonPropertyName("tool_calls_purged_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ToolCallsPurgedAt { get; init; }
}
