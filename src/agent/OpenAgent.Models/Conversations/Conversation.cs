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
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required ConversationType Type { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? VoiceSessionId { get; set; }
    public bool VoiceSessionOpen { get; set; }
}
