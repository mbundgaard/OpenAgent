namespace OpenAgent.Models.Voice;

public sealed class VoiceSessionOptions
{
    public required string ConversationId { get; init; }
    public string? Voice { get; init; }
}
