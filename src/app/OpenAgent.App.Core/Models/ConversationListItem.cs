using System.Text.Json.Serialization;

namespace OpenAgent.App.Core.Models;

/// <summary>One row as returned by GET /api/conversations. Field names mirror ConversationListItemResponse on the agent.</summary>
public sealed record ConversationListItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("last_activity")] public DateTimeOffset? LastActivity { get; init; }
    [JsonPropertyName("turn_count")] public int TurnCount { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("intention")] public string? Intention { get; init; }

    /// <summary>Effective row title — display_name wins, then intention, then id.</summary>
    [JsonIgnore]
    public string Title => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
                          : !string.IsNullOrWhiteSpace(Intention) ? Intention!
                          : Id;

    /// <summary>Effective sort key for list ordering — last_activity if known, else created_at.</summary>
    [JsonIgnore]
    public DateTimeOffset SortKey => LastActivity ?? CreatedAt;
}
