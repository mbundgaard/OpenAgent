using System.Text.Json.Serialization;

namespace OpenAgent.Models.Conversations;

public enum ConversationType
{
    Text,
    Voice,
    ScheduledTask,
    WebHook
}

/// <summary>
/// Represents a single user conversation, including its voice session state.
/// </summary>
public sealed class Conversation
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("source")]
    public required string Source { get; set; }
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<ConversationType>))]
    public required ConversationType Type { get; init; }

    /// <summary>Provider key used for this conversation (e.g. "azure-openai-text").</summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; set; }

    /// <summary>Model/deployment used for this conversation (e.g. "gpt-5.2-chat").</summary>
    [JsonPropertyName("model")]
    public required string Model { get; set; }

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
    /// Structured summary of compacted messages — topic-grouped with timestamps and message references.
    /// Null until the first compaction runs.
    /// </summary>
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; set; }

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
}
