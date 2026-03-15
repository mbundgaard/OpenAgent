using System.Text.Json.Serialization;

namespace OpenAgent.Models.Conversations;

public enum ConversationType
{
    Text,
    Voice,
    Cron,
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
    public required string Source { get; init; }
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<ConversationType>))]
    public required ConversationType Type { get; init; }
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
}
