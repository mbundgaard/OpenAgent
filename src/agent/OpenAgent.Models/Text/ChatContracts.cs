using System.Text.Json.Serialization;

namespace OpenAgent.Models.Text;

/// <summary>
/// REST chat request payload.
/// </summary>
public sealed class ChatRequest
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

/// <summary>
/// REST chat response payload.
/// </summary>
public sealed class ChatResponse
{
    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}
