using System.Text.Json.Serialization;

namespace OpenAgent.ScheduledTasks.SystemJobs;

/// <summary>
/// Persisted state for a single system job. Lives in <c>{dataPath}/config/system-jobs.json</c>
/// keyed by job name. Mirrors the runtime state of <see cref="Models.ScheduledTaskState"/> but
/// with a smaller surface — system jobs have no name/prompt/conversation, just last+next run
/// and last-error metadata.
/// </summary>
public sealed class SystemJobState
{
    [JsonPropertyName("lastRunAt")]
    public DateTimeOffset? LastRunAt { get; set; }

    [JsonPropertyName("nextRunAt")]
    public DateTimeOffset? NextRunAt { get; set; }

    [JsonPropertyName("lastStatus")]
    public string? LastStatus { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("consecutiveErrors")]
    public int ConsecutiveErrors { get; set; }
}
