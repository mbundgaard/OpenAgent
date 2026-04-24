using System.Text.Json.Serialization;

namespace OpenAgent.Models.Conversations;

/// <summary>
/// Why compaction is being triggered. Used for logging and to gate behavior
/// (e.g. only Overflow triggers a turn retry).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CompactionReason>))]
public enum CompactionReason
{
    /// <summary>Proactive trigger from exceeding the threshold after a successful turn.</summary>
    Threshold,

    /// <summary>Reactive trigger from a context-length error mid-turn. Caller retries the turn once.</summary>
    Overflow,

    /// <summary>User- or operator-initiated via /api/conversations/{id}/compact.</summary>
    Manual
}
