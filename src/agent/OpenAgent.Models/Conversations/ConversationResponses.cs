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

    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<ConversationType>))]
    public required ConversationType Type { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

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

    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<ConversationType>))]
    public required ConversationType Type { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

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
}

/// <summary>
/// Request body for updating a conversation.
/// </summary>
public sealed class UpdateConversationRequest
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("channel_chat_id")]
    public string? ChannelChatId { get; init; }
}
