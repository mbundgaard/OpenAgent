using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.ScheduledTasks.Models;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Exposes scheduled task CRUD to the LLM as callable tools. This is how the agent manages
/// its own proactive work — during a conversation you can say "remind me tomorrow at 9am" and
/// the agent will call create_scheduled_task directly, rather than you having to hit an API.
///
/// The four tools mirror the service's CRUD API 1:1 but with context-aware defaults: when
/// called from a channel conversation (e.g. Telegram), CreateScheduledTaskTool auto-fills the
/// delivery target from the current conversationId, so the user doesn't have to tell the agent
/// "reply to this chat" — it's inferred.
/// </summary>
public sealed class ScheduledTaskToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public ScheduledTaskToolHandler(ScheduledTaskService service, ILogger<ScheduledTaskToolHandler> logger)
    {
        Tools = [
            new CreateScheduledTaskTool(service, logger),
            new ListScheduledTasksTool(service),
            new UpdateScheduledTaskTool(service, logger),
            new DeleteScheduledTaskTool(service, logger)
        ];
    }
}

/// <summary>
/// Creates a new scheduled task with the given configuration.
/// Auto-detects delivery target from conversationId when the caller is a channel chat.
/// </summary>
internal sealed class CreateScheduledTaskTool(ScheduledTaskService service, ILogger logger) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_scheduled_task",
        Description = "Create a new scheduled task that runs an LLM prompt on a schedule. " +
                      "Specify cron, intervalMs, or at for one-time. " +
                      "Delivery defaults to the current channel chat if called from Telegram/WhatsApp.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "Short name for the task" },
                prompt = new { type = "string", description = "The prompt to send to the LLM on each run" },
                schedule = new
                {
                    type = "object",
                    description = "Schedule config — exactly one of cron, intervalMs, or at must be set",
                    properties = new
                    {
                        cron = new { type = "string", description = "Cron expression (5 or 6 fields)" },
                        timezone = new { type = "string", description = "IANA timezone for cron (default UTC)" },
                        intervalMs = new { type = "integer", description = "Repeat interval in milliseconds" },
                        at = new { type = "string", description = "ISO 8601 datetime for one-time execution" }
                    }
                },
                description = new { type = "string", description = "Optional longer description" },
                deleteAfterRun = new { type = "boolean", description = "If true, delete the task after one successful run" },
                delivery = new
                {
                    type = "object",
                    description = "Where to deliver results. Defaults to silent.",
                    properties = new
                    {
                        mode = new { type = "string", description = "silent, channel, or webhook" },
                        connectionId = new { type = "string", description = "Connection ID for channel delivery" },
                        chatId = new { type = "string", description = "Chat ID for channel delivery" },
                        webhookUrl = new { type = "string", description = "URL for webhook delivery" }
                    }
                }
            },
            required = new[] { "name", "prompt", "schedule" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;

            var name = args.GetProperty("name").GetString()!;
            var prompt = args.GetProperty("prompt").GetString()!;

            // Deserialize schedule
            var scheduleEl = args.GetProperty("schedule");
            var schedule = JsonSerializer.Deserialize<ScheduleConfig>(scheduleEl.GetRawText())!;

            // Optional fields
            var description = args.TryGetProperty("description", out var descEl) ? descEl.GetString() : null;
            var deleteAfterRun = args.TryGetProperty("deleteAfterRun", out var darEl) && darEl.GetBoolean();

            // Delivery — deserialize if provided, otherwise try to auto-detect from conversationId
            DeliveryConfig? delivery = null;
            if (args.TryGetProperty("delivery", out var deliveryEl))
            {
                delivery = JsonSerializer.Deserialize<DeliveryConfig>(deliveryEl.GetRawText());
            }

            // Context-aware default: if conversationId is "telegram:connId:chatId" or "whatsapp:connId:chatId",
            // auto-fill delivery connectionId and chatId when not explicitly provided
            if (delivery is null or { Mode: DeliveryMode.Channel, ConnectionId: null })
            {
                var parts = conversationId.Split(':');
                if (parts.Length >= 3 && (parts[0] == "telegram" || parts[0] == "whatsapp"))
                {
                    delivery ??= new DeliveryConfig { Mode = DeliveryMode.Channel };
                    delivery.ConnectionId ??= parts[1];
                    delivery.ChatId ??= string.Join(":", parts[2..]);
                }
            }

            var task = new ScheduledTask
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Description = description,
                Prompt = prompt,
                Schedule = schedule,
                DeleteAfterRun = deleteAfterRun,
                Delivery = delivery
            };

            await service.AddAsync(task, ct);

            return JsonSerializer.Serialize(new
            {
                status = "created",
                id = task.Id,
                name = task.Name,
                nextRunAt = task.State.NextRunAt?.ToString("o")
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create scheduled task");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}

/// <summary>
/// Lists all scheduled tasks with summary information.
/// </summary>
internal sealed class ListScheduledTasksTool(ScheduledTaskService service) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_scheduled_tasks",
        Description = "List all scheduled tasks with their status, schedule, and next run time.",
        Parameters = new
        {
            type = "object",
            properties = new { }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var tasks = await service.ListAsync(ct);

        var items = tasks.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            enabled = t.Enabled,
            schedule = FormatSchedule(t.Schedule),
            nextRunAt = t.State.NextRunAt?.ToString("o"),
            lastStatus = t.State.LastStatus?.ToString().ToLowerInvariant(),
            lastRunAt = t.State.LastRunAt?.ToString("o"),
            deliveryMode = t.Delivery?.Mode.ToString().ToLowerInvariant() ?? "silent"
        });

        return JsonSerializer.Serialize(items);
    }

    private static string FormatSchedule(ScheduleConfig schedule)
    {
        if (schedule.Cron is not null)
            return schedule.Timezone is not null ? $"cron: {schedule.Cron} ({schedule.Timezone})" : $"cron: {schedule.Cron}";
        if (schedule.IntervalMs.HasValue)
            return $"every {schedule.IntervalMs}ms";
        if (schedule.At.HasValue)
            return $"once at {schedule.At.Value:o}";
        return "unknown";
    }
}

/// <summary>
/// Updates an existing scheduled task. Only provided fields are modified.
/// </summary>
internal sealed class UpdateScheduledTaskTool(ScheduledTaskService service, ILogger logger) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "update_scheduled_task",
        Description = "Update an existing scheduled task. Only provide the fields you want to change.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                taskId = new { type = "string", description = "ID of the task to update" },
                name = new { type = "string", description = "New name" },
                prompt = new { type = "string", description = "New prompt" },
                enabled = new { type = "boolean", description = "Enable or disable the task" },
                schedule = new
                {
                    type = "object",
                    description = "New schedule config",
                    properties = new
                    {
                        cron = new { type = "string", description = "Cron expression" },
                        timezone = new { type = "string", description = "IANA timezone" },
                        intervalMs = new { type = "integer", description = "Interval in ms" },
                        at = new { type = "string", description = "ISO 8601 datetime" }
                    }
                },
                delivery = new
                {
                    type = "object",
                    description = "New delivery config",
                    properties = new
                    {
                        mode = new { type = "string", description = "silent, channel, or webhook" },
                        connectionId = new { type = "string", description = "Connection ID" },
                        chatId = new { type = "string", description = "Chat ID" },
                        webhookUrl = new { type = "string", description = "Webhook URL" }
                    }
                }
            },
            required = new[] { "taskId" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            var taskId = args.GetProperty("taskId").GetString()!;

            // Build a patch action that applies only the provided fields
            await service.UpdateAsync(taskId, task =>
            {
                if (args.TryGetProperty("name", out var nameEl))
                    task.Name = nameEl.GetString()!;

                if (args.TryGetProperty("prompt", out var promptEl))
                    task.Prompt = promptEl.GetString()!;

                if (args.TryGetProperty("enabled", out var enabledEl))
                    task.Enabled = enabledEl.GetBoolean();

                if (args.TryGetProperty("schedule", out var scheduleEl))
                    task.Schedule = JsonSerializer.Deserialize<ScheduleConfig>(scheduleEl.GetRawText())!;

                if (args.TryGetProperty("delivery", out var deliveryEl))
                    task.Delivery = JsonSerializer.Deserialize<DeliveryConfig>(deliveryEl.GetRawText());
            }, ct);

            // Re-fetch the task to get the updated name
            var updated = await service.GetAsync(taskId, ct);

            return JsonSerializer.Serialize(new
            {
                status = "updated",
                id = taskId,
                name = updated?.Name ?? "unknown"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update scheduled task");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}

/// <summary>
/// Deletes a scheduled task by ID.
/// </summary>
internal sealed class DeleteScheduledTaskTool(ScheduledTaskService service, ILogger logger) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "delete_scheduled_task",
        Description = "Delete a scheduled task by ID.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                taskId = new { type = "string", description = "ID of the task to delete" }
            },
            required = new[] { "taskId" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            var taskId = args.GetProperty("taskId").GetString()!;

            await service.RemoveAsync(taskId, ct);

            return JsonSerializer.Serialize(new
            {
                status = "deleted",
                taskId
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete scheduled task");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
