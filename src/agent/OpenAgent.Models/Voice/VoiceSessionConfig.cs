namespace OpenAgent.Models.Voice;

/// <summary>
/// Options passed when starting a voice session — ties it to a conversation and allows voice selection.
/// </summary>
public sealed class VoiceSessionOptions
{
    public required string ConversationId { get; init; }
    public string? Voice { get; init; }
}
