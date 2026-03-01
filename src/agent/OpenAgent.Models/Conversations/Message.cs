namespace OpenAgent.Models.Conversations;

/// <summary>
/// A single message within a conversation (user or assistant), with role, content, and timestamp.
/// </summary>
public sealed class Message
{
    public required string Id { get; init; }
    public required string ConversationId { get; init; }
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
