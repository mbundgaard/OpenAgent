namespace OpenAgent.Models.Voice;

public sealed class VoiceSessionConfig
{
    public required string ConversationId { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Voice { get; init; }
}
