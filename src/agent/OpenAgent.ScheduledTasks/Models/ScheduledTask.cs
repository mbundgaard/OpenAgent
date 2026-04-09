using System.Text.Json.Serialization;

namespace OpenAgent.ScheduledTasks.Models;

/// <summary>
/// The definition of a proactive agent action — what to run, when, and in which conversation.
/// A ScheduledTask turns a prompt into a scheduled LLM completion inside a conversation. The
/// conversation owns its own output delivery (channel-bound conversations deliver to the channel,
/// unbound conversations are silent), so the task itself has no delivery config.
///
/// State (NextRunAt, LastRunAt, errors) is kept on the nested State object — separated from the
/// definition so the engine can update execution history without touching user-editable fields.
/// </summary>
public sealed class ScheduledTask
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("deleteAfterRun")]
    public bool DeleteAfterRun { get; set; }

    [JsonPropertyName("schedule")]
    public required ScheduleConfig Schedule { get; set; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; set; }

    /// <summary>
    /// Conversation this task runs in. Null on create → executor generates a fresh
    /// GUID on first run and writes it back here. Can be set explicitly to an existing
    /// conversation ID (e.g. a Telegram chat) so the task runs "inside" that chat and
    /// has full context for replies. The conversation owns delivery: channel-bound
    /// conversations deliver to the channel; unbound conversations are silent.
    /// </summary>
    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("state")]
    public ScheduledTaskState State { get; set; } = new();
}

/// <summary>
/// Defines WHEN a task runs. Three mutually-exclusive modes:
/// Cron (recurring with calendar semantics, e.g. "every weekday at 9am"),
/// IntervalMs (fixed delay between runs, e.g. every 5 minutes),
/// or At (one-shot at a specific timestamp, typically paired with DeleteAfterRun for reminders).
/// Timezone only applies to Cron — interval and one-shot are absolute UTC.
/// Validated by ScheduleCalculator.Validate before persistence.
/// </summary>
public sealed class ScheduleConfig
{
    [JsonPropertyName("cron")]
    public string? Cron { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("intervalMs")]
    public long? IntervalMs { get; set; }

    [JsonPropertyName("at")]
    public DateTimeOffset? At { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TaskRunStatus>))]
public enum TaskRunStatus
{
    Success,
    Error
}

/// <summary>
/// Mutable runtime state owned by ScheduledTaskService — NEVER set from API/tools.
/// Separated from the task definition so execution updates (NextRunAt, LastRunAt, errors)
/// don't collide with user edits to name/prompt/schedule. The API surface ignores State on
/// inbound payloads; only the engine writes here. ConsecutiveErrors tracks failure streaks
/// for future automatic disable/backoff logic.
/// </summary>
public sealed class ScheduledTaskState
{
    [JsonPropertyName("nextRunAt")]
    public DateTimeOffset? NextRunAt { get; set; }

    [JsonPropertyName("lastRunAt")]
    public DateTimeOffset? LastRunAt { get; set; }

    [JsonPropertyName("lastStatus")]
    public TaskRunStatus? LastStatus { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("consecutiveErrors")]
    public int ConsecutiveErrors { get; set; }
}

/// <summary>
/// Root object for the scheduled-tasks.json file.
/// </summary>
public sealed class ScheduledTaskFile
{
    [JsonPropertyName("tasks")]
    public List<ScheduledTask> Tasks { get; set; } = [];
}
