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
}

/// <summary>
/// Minimal conversation payload returned by API endpoints.
/// </summary>
public sealed class ConversationIdResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}
