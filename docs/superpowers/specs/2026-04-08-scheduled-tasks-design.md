# Scheduled Tasks — Design Spec

Make the agent proactive. Scheduled tasks let the agent wake up on a schedule, respond to webhooks, or run reflective heartbeats — and deliver results to channels, webhooks, or silent conversation history.

## Requirements

- **Three trigger modes:** cron expressions, fixed intervals, one-shot timestamps
- **Per-job delivery:** silent (conversation only), channel (Telegram/WhatsApp), webhook callback
- **Agent-managed:** The agent creates/edits/deletes tasks conversationally via tools
- **API/UI-managed:** REST endpoints for direct CRUD control
- **Webhook triggers:** External events fire a task immediately with context payload
- **Prompt-only execution initially**, data model ready for full agent turn config (model, tools, timeout)
- **JSON file storage** at `{dataPath}/config/scheduled-tasks.json`
- **Rename** `ConversationType.Cron` to `ConversationType.ScheduledTask` — no backwards compatibility

## Data Model

### ScheduledTask

```csharp
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
```

### ScheduleConfig

Exactly one property is set per task.

```csharp
public sealed class ScheduleConfig
{
    [JsonPropertyName("cron")]
    public string? Cron { get; set; }                  // "0 9 * * *"

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }              // "Europe/Copenhagen", defaults to UTC

    [JsonPropertyName("intervalMs")]
    public long? IntervalMs { get; set; }              // Fixed interval in milliseconds

    [JsonPropertyName("at")]
    public DateTimeOffset? At { get; set; }            // One-shot timestamp
}
```

### DeliveryConfig

```csharp
public sealed class DeliveryConfig
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "silent";       // "silent" | "channel" | "webhook"

    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; set; }          // Channel connection to deliver through

    [JsonPropertyName("chatId")]
    public string? ChatId { get; set; }                // Target chat on that channel

    [JsonPropertyName("webhookUrl")]
    public string? WebhookUrl { get; set; }            // For webhook delivery mode
}
```

### AgentTurnConfig (reserved, unused initially)

```csharp
public sealed class AgentTurnConfig
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }

    [JsonPropertyName("tools")]
    public List<string>? Tools { get; set; }           // Tool name whitelist
}
```

### ScheduledTaskState

Runtime state, updated by the engine only. Not editable via API.

```csharp
public sealed class ScheduledTaskState
{
    [JsonPropertyName("nextRunAt")]
    public DateTimeOffset? NextRunAt { get; set; }

    [JsonPropertyName("lastRunAt")]
    public DateTimeOffset? LastRunAt { get; set; }

    [JsonPropertyName("lastStatus")]
    public string? LastStatus { get; set; }            // "success" | "error"

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("consecutiveErrors")]
    public int ConsecutiveErrors { get; set; }
}
```

## Engine: ScheduledTaskService

`ScheduledTaskService : IHostedService` owns the full lifecycle.

### Startup

1. Load tasks from `scheduled-tasks.json`
2. Compute `NextRunAt` for all enabled tasks
3. Check for missed runs (tasks where `NextRunAt < now`) — execute with staggered delays
4. Arm the timer

### Timer Loop

- Ticks every 30 seconds
- Finds tasks where `NextRunAt <= now && Enabled`
- Executes up to 3 tasks concurrently (configurable `MaxConcurrentRuns`)
- After execution: update state, recompute `NextRunAt`, persist to JSON

### Execution Flow (ScheduledTaskExecutor)

For each due task:

1. Resolve conversation via `IConversationStore.GetOrCreate("scheduledtask:{taskId}", "scheduledtask", ConversationType.ScheduledTask)`
2. Build user message: `new Message { Role = "user", Content = task.Prompt }`
3. Resolve text provider via `Func<string, ILlmTextProvider>` using `AgentConfig.TextProvider`
4. Call `provider.CompleteAsync(conversation, message, ct)`
5. Collect `CompletionEvent` stream — extract full assistant response text
6. Route to `DeliveryRouter` based on `task.Delivery.Mode`
7. Update task state: `LastRunAt = now`, `LastStatus = "success"`, `ConsecutiveErrors = 0`, recompute `NextRunAt`
8. If `DeleteAfterRun == true`, remove the task

### Error Handling

- On failure: `LastStatus = "error"`, `LastError = exception message`, `ConsecutiveErrors++`
- No automatic retry or disable — simple for now
- Next scheduled run proceeds normally

### CRUD Methods

All mutations called by both agent tools and API endpoints:

| Method | Description |
|--------|-------------|
| `Add(ScheduledTask)` | Validate, assign ID if missing, persist, rearm timer |
| `Update(string id, patch)` | Merge changes, persist, recompute NextRunAt, rearm |
| `Remove(string id)` | Delete from store, persist, rearm |
| `List()` | Return all tasks |
| `Get(string id)` | Return single task |
| `RunNow(string id)` | Execute immediately regardless of schedule |

All mutations persist the full task list to JSON and rearm the timer.

## Delivery System

### DeliveryRouter

Routes completed task output based on `DeliveryConfig.Mode`:

**silent** — No action beyond conversation persistence (already done by the provider).

**channel** — Resolve `IChannelProvider` from `ConnectionManager.GetProvider(connectionId)`. Cast to `IOutboundSender`. Call `SendMessageAsync(chatId, responseText, ct)`. If provider doesn't implement `IOutboundSender`, log warning and fall back to silent.

**webhook** — HTTP POST to `delivery.WebhookUrl`:

```json
{
    "taskId": "...",
    "taskName": "...",
    "status": "success",
    "response": "The assistant's full response text",
    "executedAt": "2026-04-08T09:00:00Z"
}
```

10-second timeout. Failure logged in `LastError` but does not affect task state (`LastStatus` reflects LLM execution, not delivery).

### IOutboundSender Interface

New interface in `OpenAgent.Contracts`:

```csharp
public interface IOutboundSender
{
    Task SendMessageAsync(string chatId, string text, CancellationToken ct = default);
}
```

Implemented by `TelegramChannelProvider` (via `ITelegramBotClient.SendMessage`) and `WhatsAppChannelProvider` (via Baileys bridge `sendMessage` command). Channel providers that don't support outbound simply don't implement the interface.

## Agent Tools

`ScheduledTaskToolHandler : IToolHandler` exposes four tools:

### create_scheduled_task

Creates a new scheduled task. Parameters: `name`, `prompt`, `schedule`, `deleteAfterRun`, `delivery`. Returns the created task with its ID.

**Context-aware defaults:** The tool receives `conversationId`. If the conversation is a channel conversation (e.g. `telegram:conn1:12345`), the tool parses out connectionId and chatId and uses them as delivery defaults when `delivery.mode == "channel"` but no explicit connectionId/chatId is provided.

### list_scheduled_tasks

Lists all tasks with name, enabled status, schedule summary, next run time, and last status. No parameters.

### update_scheduled_task

Updates an existing task. Parameters: `taskId` + any fields to change. Partial update — only provided fields are modified.

### delete_scheduled_task

Deletes a task by ID. Returns confirmation.

## API Endpoints

All in `ScheduledTaskEndpoints.cs` in `OpenAgent.Api/Endpoints/`:

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/scheduled-tasks` | List all tasks |
| `GET` | `/api/scheduled-tasks/{taskId}` | Get single task with state |
| `POST` | `/api/scheduled-tasks` | Create task |
| `PUT` | `/api/scheduled-tasks/{taskId}` | Update task |
| `DELETE` | `/api/scheduled-tasks/{taskId}` | Delete task |
| `POST` | `/api/scheduled-tasks/{taskId}/run` | Execute immediately |
| `POST` | `/api/webhooks/{taskId}` | Trigger from external event |

All endpoints require API key authentication.

### Webhook Trigger

`POST /api/webhooks/{taskId}` accepts an optional JSON body. The body is serialized and appended to the task's prompt as context:

```
{original task prompt}

<webhook_context>
{serialized webhook payload}
</webhook_context>
```

The task executes immediately with the augmented prompt. The original task prompt is not modified — the context is only for this execution.

## Project Structure

### New Project: OpenAgent.ScheduledTasks

```
src/agent/
  OpenAgent.ScheduledTasks/
    Models/
      ScheduledTask.cs              # All model classes
    Storage/
      ScheduledTaskStore.cs         # Load/save JSON file, in-memory cache
    ScheduledTaskService.cs         # IHostedService — timer loop, CRUD, lifecycle
    ScheduledTaskExecutor.cs        # Runs a single task end-to-end
    DeliveryRouter.cs               # Routes output to silent/channel/webhook
    ScheduledTaskToolHandler.cs     # IToolHandler — four agent tools
    ServiceCollectionExtensions.cs  # AddScheduledTasks(dataPath) extension
```

### Changes to Existing Projects

**OpenAgent.Contracts:**
- Add `IOutboundSender.cs`

**OpenAgent.Models:**
- Rename `ConversationType.Cron` to `ConversationType.ScheduledTask` in enum
- Update all references

**OpenAgent.Api:**
- Add `Endpoints/ScheduledTaskEndpoints.cs`

**OpenAgent (host):**
- `Program.cs`: call `AddScheduledTasks(dataPath)` and `MapScheduledTaskEndpoints()`

**OpenAgent.Channel.Telegram:**
- `TelegramChannelProvider` implements `IOutboundSender`

**OpenAgent.Channel.WhatsApp:**
- `WhatsAppChannelProvider` implements `IOutboundSender`

**OpenAgent.SystemPromptBuilder:**
- Update `FileMap` references from `ConversationType.Cron` to `ConversationType.ScheduledTask`

### Dependencies

- `OpenAgent.ScheduledTasks` references `OpenAgent.Contracts`, `OpenAgent.Models`
- `OpenAgent.ScheduledTasks` depends on `Cronos` NuGet package for cron expression parsing
- `OpenAgent` (host) references `OpenAgent.ScheduledTasks`
- `OpenAgent.Api` references `OpenAgent.ScheduledTasks`

## Cron Expression Parsing

Uses the `Cronos` NuGet package — lightweight, .NET-native, supports timezones and 5/6-field expressions.

```csharp
var expression = CronExpression.Parse("0 9 * * *");
var nextRun = expression.GetNextOccurrence(DateTimeOffset.UtcNow, timeZone);
```

## What's NOT in This Spec

- **Web UI for task management** — API-first, UI follows later
- **Full agent turn execution** — `AgentTurnConfig` is in the model but unused. Separate spec when needed.
- **Built-in heartbeat mode** — Heartbeat is just a scheduled task with a reflective prompt. No special machinery.
- **Retry with backoff** — Failed tasks just increment `ConsecutiveErrors`. Retry logic is a future enhancement.
- **Staggering** — No deterministic hash-based stagger. The 30s timer tick and max concurrency cap are sufficient for now.
- **Task execution history/audit log** — Only last run state is stored. History is in the conversation messages.
