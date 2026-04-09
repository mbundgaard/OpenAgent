using System.Text.Json.Serialization;

namespace OpenAgent.ScheduledTasks.Models;

/// <summary>
/// The definition of a proactive agent action — what to run, when, and where to deliver the result.
/// A ScheduledTask turns a prompt into a scheduled LLM completion. Each task gets its own dedicated
/// conversation (scheduledtask:{Id}) so history accumulates naturally and benefits from compaction.
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

    [JsonPropertyName("agentTurn")]
    public AgentTurnConfig? AgentTurn { get; set; }

    [JsonPropertyName("delivery")]
    public DeliveryConfig? Delivery { get; set; }

    /// <summary>
    /// Conversation this task runs in. Null on create → executor generates a fresh
    /// GUID on first run and writes it back here. Can be set explicitly to an existing
    /// conversation ID (e.g. a Telegram chat) so the task runs "inside" that chat and
    /// has full context for replies.
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

[JsonConverter(typeof(JsonStringEnumConverter<DeliveryMode>))]
public enum DeliveryMode
{
    Silent,
    Channel,
    Webhook
}

/// <summary>
/// Where the assistant response goes after a task runs. Three modes:
/// Silent (default — result stays in the task's conversation history, viewable in the web UI),
/// Channel (proactively message a specific chat via a running channel connection — agent messages you),
/// Webhook (POST the result as JSON to an external URL — integrate with other systems).
/// Channel delivery requires the target provider to implement IOutboundSender, otherwise falls back to silent.
/// </summary>
public sealed class DeliveryConfig
{
    [JsonPropertyName("mode")]
    public DeliveryMode Mode { get; set; } = DeliveryMode.Silent;

    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; set; }

    [JsonPropertyName("chatId")]
    public string? ChatId { get; set; }

    [JsonPropertyName("webhookUrl")]
    public string? WebhookUrl { get; set; }
}

/// <summary>
/// Reserved for future use — will let individual tasks override the default LLM setup.
/// Today scheduled tasks run with the global AgentConfig (provider, model, full tool set).
/// Once enabled this will allow per-task model selection, tool whitelisting, custom timeouts,
/// and lightweight isolated runs. Present in the data model now so existing tasks won't need
/// migration when the feature ships.
/// </summary>
public sealed class AgentTurnConfig
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }
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
