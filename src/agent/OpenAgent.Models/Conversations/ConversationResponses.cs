using System.Text.Json.Serialization;

namespace OpenAgent.Models.Conversations;

/// <summary>
/// Conversation list item payload returned by API endpoints.
/// </summary>
public sealed class ConversationListItemResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("text_provider")]
    public required string TextProvider { get; init; }

    [JsonPropertyName("text_model")]
    public required string TextModel { get; init; }

    [JsonPropertyName("voice_provider")]
    public required string VoiceProvider { get; init; }

    [JsonPropertyName("voice_model")]
    public required string VoiceModel { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("total_prompt_tokens")]
    public long TotalPromptTokens { get; init; }

    [JsonPropertyName("total_completion_tokens")]
    public long TotalCompletionTokens { get; init; }

    [JsonPropertyName("turn_count")]
    public int TurnCount { get; init; }

    [JsonPropertyName("last_activity")]
    public DateTimeOffset? LastActivity { get; init; }

    [JsonPropertyName("active_skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ActiveSkills { get; init; }

    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("intention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Intention { get; init; }

    [JsonPropertyName("mention_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MentionFilter { get; init; }
}

/// <summary>
/// Full conversation detail payload.
/// </summary>
public sealed class ConversationDetailResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("text_provider")]
    public required string TextProvider { get; init; }

    [JsonPropertyName("text_model")]
    public required string TextModel { get; init; }

    [JsonPropertyName("voice_provider")]
    public required string VoiceProvider { get; init; }

    [JsonPropertyName("voice_model")]
    public required string VoiceModel { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("total_prompt_tokens")]
    public long TotalPromptTokens { get; init; }

    [JsonPropertyName("total_completion_tokens")]
    public long TotalCompletionTokens { get; init; }

    [JsonPropertyName("turn_count")]
    public int TurnCount { get; init; }

    [JsonPropertyName("last_activity")]
    public DateTimeOffset? LastActivity { get; init; }

    [JsonPropertyName("voice_session_id")]
    public string? VoiceSessionId { get; init; }

    [JsonPropertyName("voice_session_open")]
    public bool VoiceSessionOpen { get; init; }

    [JsonPropertyName("compaction_running")]
    public bool CompactionRunning { get; init; }

    [JsonPropertyName("active_skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ActiveSkills { get; init; }

    [JsonPropertyName("channel_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelType { get; init; }

    [JsonPropertyName("connection_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionId { get; init; }

    [JsonPropertyName("channel_chat_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChannelChatId { get; init; }

    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("intention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Intention { get; init; }

    [JsonPropertyName("mention_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MentionFilter { get; init; }
}

/// <summary>
/// Request body for updating a conversation.
/// Properties that are absent (null) leave the corresponding field unchanged.
/// Use empty string ("") to explicitly clear a nullable field like Intention.
/// </summary>
public sealed class UpdateConversationRequest
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("text_provider")]
    public string? TextProvider { get; init; }

    [JsonPropertyName("text_model")]
    public string? TextModel { get; init; }

    [JsonPropertyName("voice_provider")]
    public string? VoiceProvider { get; init; }

    [JsonPropertyName("voice_model")]
    public string? VoiceModel { get; init; }

    [JsonPropertyName("channel_chat_id")]
    public string? ChannelChatId { get; init; }

    [JsonPropertyName("intention")]
    public string? Intention { get; init; }

    [JsonPropertyName("mention_filter")]
    public List<string>? MentionFilter { get; init; }
}
