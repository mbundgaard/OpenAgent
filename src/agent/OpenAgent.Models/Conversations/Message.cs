using System.Text.Json.Serialization;

namespace OpenAgent.Models.Conversations;

/// <summary>
/// A single message within a conversation (user or assistant), with role, content, and timestamp.
/// </summary>
public sealed class Message
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; init; }
    [JsonPropertyName("role")]
    public required string Role { get; init; }
    [JsonPropertyName("content")]
    public required string Content { get; init; }
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
