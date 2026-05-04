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
    /// Identifier of the user who sent this message — phone number for WhatsApp / Telnyx,
    /// numeric Telegram user ID for Telegram, etc. Channel-namespaced is unnecessary because
    /// the conversation already encodes the channel context. Null for assistant / tool messages
    /// and for inbound paths that have no per-user identity (REST, webhook, scheduled tasks).
    /// At LLM-context build time, providers wrap user content with a &lt;from id="…"&gt; tag when
    /// this field is non-null, so the agent can disambiguate speakers in group chats.
    /// </summary>
    [JsonPropertyName("sender")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sender { get; init; }

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
    /// Relative path to the full tool result on disk, scoped under
    /// {dataPath}/conversations/{conversationId}/. Null for non-tool messages and for rows
    /// written before this migration. When null, the summary in Content is the only record.
    /// </summary>
    [JsonPropertyName("tool_result_ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolResultRef { get; init; }

    /// <summary>
    /// Full tool result content. Not persisted directly — when set on a new message, the store
    /// saves it to disk and populates ToolResultRef. When loaded via
    /// IConversationStore.GetMessages(..., includeToolResultBlobs: true), the store reads the
    /// blob and populates this field. Null otherwise.
    /// </summary>
    [JsonIgnore]
    public string? FullToolResult { get; init; }
}
