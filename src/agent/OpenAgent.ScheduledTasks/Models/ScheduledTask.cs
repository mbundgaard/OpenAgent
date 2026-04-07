using System.Text.Json.Serialization;

namespace OpenAgent.ScheduledTasks.Models;

/// <summary>
/// A task that executes on a schedule, delivering LLM responses to configured targets.
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

    [JsonPropertyName("state")]
    public ScheduledTaskState State { get; set; } = new();
}

/// <summary>
/// Schedule configuration. Exactly one of Cron, IntervalMs, or At must be set.
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
/// Configures where task results are delivered.
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
/// Reserved for future full agent turn configuration.
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
/// Runtime state updated by the engine only. Not editable via API.
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
