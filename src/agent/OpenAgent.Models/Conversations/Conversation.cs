using System.Text.Json.Serialization;

namespace OpenAgent.Models.Conversations;

/// <summary>
/// Represents a single user conversation, including its voice session state.
/// </summary>
public sealed class Conversation
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("source")]
    public required string Source { get; set; }

    /// <summary>Text-LLM provider key used for this conversation (e.g. "azure-openai-text").</summary>
    [JsonPropertyName("text_provider")]
    public required string TextProvider { get; set; }

    /// <summary>Text-LLM model/deployment used for this conversation (e.g. "gpt-5.2-chat").</summary>
    [JsonPropertyName("text_model")]
    public required string TextModel { get; set; }

    /// <summary>Voice-LLM provider key used for this conversation (e.g. "azure-openai-voice").</summary>
    [JsonPropertyName("voice_provider")]
    public required string VoiceProvider { get; set; }

    /// <summary>Voice-LLM model/deployment used for this conversation (e.g. "gpt-realtime").</summary>
    [JsonPropertyName("voice_model")]
    public required string VoiceModel { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("voice_session_id")]
    public string? VoiceSessionId { get; set; }
    [JsonPropertyName("voice_session_open")]
    public bool VoiceSessionOpen { get; set; }

    /// <summary>
    /// Token count from the most recent LLM prompt. Used to determine when compaction is needed.
    /// </summary>
    [JsonPropertyName("last_prompt_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LastPromptTokens { get; set; }

    /// <summary>
    /// Cached context window size in tokens for the active model. Populated lazily by
    /// providers on the first turn after a model switch (or on first use of a new
    /// conversation). Used by the compaction threshold so it scales with the model
    /// rather than a global constant. Null until first populated.
    /// </summary>
    [JsonPropertyName("context_window_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextWindowTokens { get; set; }

    /// <summary>
    /// Structured summary of compacted messages — topic-grouped with timestamps and message references.
    /// Null until the first compaction runs.
    /// </summary>
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; set; }

    /// <summary>
    /// One-deep backup of <see cref="Context"/> as it stood immediately before the most
    /// recent compaction overwrote it. Lets us inspect what the prior summary looked like
    /// when a compaction looks suspect. Null on a fresh conversation or before any
    /// compaction has rotated.
    /// </summary>
    [JsonPropertyName("previous_context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousContext { get; set; }

    /// <summary>
    /// SQLite rowid of the last message included in the compaction summary.
    /// Messages with rowid > this value are live; messages up to and including it are compacted.
    /// Null means no compaction has occurred.
    /// </summary>
    [JsonPropertyName("compacted_up_to_row_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CompactedUpToRowId { get; set; }

    /// <summary>
    /// True while a compaction thread is running — prevents concurrent compaction.
    /// </summary>
    [JsonPropertyName("compaction_running")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CompactionRunning { get; set; }

    /// <summary>Cumulative prompt tokens across all turns.</summary>
    [JsonPropertyName("total_prompt_tokens")]
    public long TotalPromptTokens { get; set; }

    /// <summary>Cumulative completion tokens across all turns.</summary>
    [JsonPropertyName("total_completion_tokens")]
    public long TotalCompletionTokens { get; set; }

    /// <summary>Number of completed request/response turns.</summary>
    [JsonPropertyName("turn_count")]
    public int TurnCount { get; set; }

    /// <summary>When the last message was sent or received.</summary>
    [JsonPropertyName("last_activity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastActivity { get; set; }

    /// <summary>
    /// Names of skills currently active in this conversation.
    /// Active skill instructions are appended to the system prompt.
    /// </summary>
    [JsonPropertyName("active_skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ActiveSkills { get; set; }

    /// <summary>
    /// Channel type this conversation is bound to (e.g. "telegram", "whatsapp"),
    /// or null for app/scheduled-task conversations with no channel binding.
    /// Together with ConnectionId and ChannelChatId, identifies the external chat.
    /// </summary>
    [JsonPropertyName("channel_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelType { get; set; }

    /// <summary>
    /// ID of the channel connection (matches Connection.Id in connections.json)
    /// that owns this conversation. Null for non-channel conversations.
    /// </summary>
    [JsonPropertyName("connection_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionId { get; set; }

    /// <summary>
    /// External platform chat ID (e.g. Telegram chat ID, WhatsApp JID) that this
    /// conversation represents. Null for non-channel conversations.
    /// </summary>
    [JsonPropertyName("channel_chat_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelChatId { get; set; }

    /// <summary>
    /// Human-readable label for this conversation (e.g. "DM: Martin Bundgaard", "Group: Fitness").
    /// Populated by channel providers from platform metadata; null for app/scheduled-task conversations.
    /// </summary>
    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Topic or purpose that scopes the conversation. Injected into the system prompt
    /// to keep the agent anchored on the intended subject across turns.
    /// </summary>
    [JsonPropertyName("intention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Intention { get; set; }

    /// <summary>
    /// Case-insensitive trigger names for the mention filter. When non-empty,
    /// inbound user text that does not contain any of these names (substring match)
    /// is silently dropped before persistence or LLM invocation.
    /// Null or empty means "reply to all" — the default behavior.
    /// </summary>
    [JsonPropertyName("mention_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MentionFilter { get; set; }
}
