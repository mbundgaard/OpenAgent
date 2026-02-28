namespace OpenAgent3.Api.Conversations;

public sealed class Conversation
{
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? VoiceSessionId { get; set; }
}
