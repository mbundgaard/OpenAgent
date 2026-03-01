namespace OpenAgent.Models.Voice;

public sealed class VoiceSessionOptions
{
    public required string ConversationId { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Voice { get; init; }
    public IReadOnlyList<VoiceToolDefinition>? Tools { get; init; }
}

public sealed class VoiceToolDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required object Parameters { get; init; } // JSON Schema object
}
