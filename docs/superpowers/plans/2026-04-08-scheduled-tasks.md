# Scheduled Tasks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the agent proactive by adding scheduled tasks — cron jobs, intervals, one-shot timers, and webhook triggers that execute LLM completions and deliver results to channels or webhooks.

**Architecture:** New `OpenAgent.ScheduledTasks` project with `ScheduledTaskService` (IHostedService) managing a timer loop, JSON file persistence, and task execution via existing `ILlmTextProvider.CompleteAsync`. Delivery routes output through a new `IOutboundSender` interface on channel providers. Agent tools and REST endpoints provide CRUD access.

**Tech Stack:** .NET 10, Cronos (cron parsing), System.Threading.Timer, SemaphoreSlim, System.Text.Json, xUnit + WebApplicationFactory

**Spec:** `docs/superpowers/specs/2026-04-08-scheduled-tasks-design.md`

---

## File Map

### New Files

| File | Responsibility |
|------|---------------|
| `src/agent/OpenAgent.ScheduledTasks/OpenAgent.ScheduledTasks.csproj` | Project file — references Contracts, Models; depends on Cronos |
| `src/agent/OpenAgent.ScheduledTasks/Models/ScheduledTask.cs` | All model classes: ScheduledTask, ScheduleConfig, DeliveryConfig, AgentTurnConfig, ScheduledTaskState, enums |
| `src/agent/OpenAgent.ScheduledTasks/Storage/ScheduledTaskStore.cs` | Load/save JSON file, in-memory cache, file lock |
| `src/agent/OpenAgent.ScheduledTasks/ScheduleCalculator.cs` | Compute NextRunAt from ScheduleConfig using Cronos |
| `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskExecutor.cs` | Execute a single task: build message, call provider, collect response |
| `src/agent/OpenAgent.ScheduledTasks/DeliveryRouter.cs` | Route output to silent/channel/webhook |
| `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs` | IHostedService — timer loop, CRUD methods, lifecycle |
| `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskToolHandler.cs` | IToolHandler with 4 agent tools |
| `src/agent/OpenAgent.ScheduledTasks/ServiceCollectionExtensions.cs` | AddScheduledTasks() DI extension |
| `src/agent/OpenAgent.Contracts/IOutboundSender.cs` | Outbound message interface for channel providers |
| `src/agent/OpenAgent.Api/Endpoints/ScheduledTaskEndpoints.cs` | REST CRUD + trigger endpoints |

### Modified Files

| File | Change |
|------|--------|
| `src/agent/OpenAgent.Models/Conversations/Conversation.cs` | Rename `Cron` to `ScheduledTask` in ConversationType enum |
| `src/agent/OpenAgent/SystemPromptBuilder.cs` | Update FileMap references |
| `src/agent/OpenAgent/Program.cs` | Wire DI and endpoints |
| `src/agent/OpenAgent.sln` | Add new project |
| `src/agent/Directory.Packages.props` | Add Cronos package version |
| `src/agent/OpenAgent/OpenAgent.csproj` | Add project reference |
| `src/agent/OpenAgent.Api/OpenAgent.Api.csproj` | Add project reference |
| `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs` | Implement IOutboundSender |
| `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs` | Implement IOutboundSender |
| `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` | Add project reference |

---

## Task 1: Rename ConversationType.Cron to ScheduledTask

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Conversation.cs:5-11`
- Modify: `src/agent/OpenAgent/SystemPromptBuilder.cs:23-32`
- Modify: all files referencing `ConversationType.Cron`

- [ ] **Step 1: Find all references to ConversationType.Cron**

Run: `cd src/agent && grep -r "ConversationType\.Cron\|ConversationType\.WebHook" --include="*.cs" -l`

This identifies every file that needs updating.

- [ ] **Step 2: Rename in the enum**

In `src/agent/OpenAgent.Models/Conversations/Conversation.cs`, change:

```csharp
public enum ConversationType
{
    Text,
    Voice,
    Cron,
    WebHook
}
```

to:

```csharp
public enum ConversationType
{
    Text,
    Voice,
    ScheduledTask,
    WebHook
}
```

- [ ] **Step 3: Update SystemPromptBuilder FileMap**

In `src/agent/OpenAgent/SystemPromptBuilder.cs`, replace all `ConversationType.Cron` with `ConversationType.ScheduledTask` in the FileMap array (lines 23-32).

- [ ] **Step 4: Update all other references**

Search results from step 1 — update each file. This may include test files, endpoint files, or UI models that reference `ConversationType.Cron`.

- [ ] **Step 5: Build and verify**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds with no errors.

- [ ] **Step 6: Run tests**

Run: `cd src/agent && dotnet test`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor: rename ConversationType.Cron to ScheduledTask"
```

---

## Task 2: Add IOutboundSender Interface to Contracts

**Files:**
- Create: `src/agent/OpenAgent.Contracts/IOutboundSender.cs`

- [ ] **Step 1: Create the interface**

Create `src/agent/OpenAgent.Contracts/IOutboundSender.cs`:

```csharp
namespace OpenAgent.Contracts;

/// <summary>
/// Sends outbound messages to a channel. Implemented by channel providers
/// that support proactive messaging (e.g. Telegram, WhatsApp).
/// </summary>
public interface IOutboundSender
{
    /// <summary>Sends a text message to the specified chat.</summary>
    Task SendMessageAsync(string chatId, string text, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Contracts/IOutboundSender.cs
git commit -m "feat: add IOutboundSender interface for proactive channel messaging"
```

---

## Task 3: Implement IOutboundSender on TelegramChannelProvider

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs:15`

- [ ] **Step 1: Add interface to class declaration**

In `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs`, change:

```csharp
public sealed class TelegramChannelProvider : IChannelProvider
```

to:

```csharp
public sealed class TelegramChannelProvider : IChannelProvider, IOutboundSender
```

- [ ] **Step 2: Implement SendMessageAsync**

Add this method to the class:

```csharp
/// <summary>Sends a proactive text message to a Telegram chat.</summary>
public async Task SendMessageAsync(string chatId, string text, CancellationToken ct = default)
{
    if (_botClient is null)
        throw new InvalidOperationException("Telegram bot client is not initialized. Start the provider first.");

    await _botClient.SendMessage(long.Parse(chatId), text, cancellationToken: ct);
}
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs
git commit -m "feat: implement IOutboundSender on TelegramChannelProvider"
```

---

## Task 4: Implement IOutboundSender on WhatsAppChannelProvider

**Files:**
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs:30`

- [ ] **Step 1: Add interface to class declaration**

In `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs`, change:

```csharp
public sealed class WhatsAppChannelProvider : IChannelProvider, IAsyncDisposable
```

to:

```csharp
public sealed class WhatsAppChannelProvider : IChannelProvider, IOutboundSender, IAsyncDisposable
```

- [ ] **Step 2: Implement SendMessageAsync**

The provider already has a `WhatsAppNodeProcessSender` with `SendTextAsync(chatId, text)`. Add this method to the main class, delegating to the internal sender:

```csharp
/// <summary>Sends a proactive text message to a WhatsApp chat.</summary>
public async Task SendMessageAsync(string chatId, string text, CancellationToken ct = default)
{
    if (_nodeProcess is null)
        throw new InvalidOperationException("WhatsApp bridge is not connected. Start the provider first.");

    await _nodeProcess.WriteAsync(WhatsAppNodeProcess.FormatSendCommand(chatId, text));
}
```

Check the exact field name for the node process — it may be `_process` or `_nodeProcess`. Use whatever the class already uses.

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs
git commit -m "feat: implement IOutboundSender on WhatsAppChannelProvider"
```

---

## Task 5: Create OpenAgent.ScheduledTasks Project and Models

**Files:**
- Create: `src/agent/OpenAgent.ScheduledTasks/OpenAgent.ScheduledTasks.csproj`
- Create: `src/agent/OpenAgent.ScheduledTasks/Models/ScheduledTask.cs`
- Modify: `src/agent/Directory.Packages.props`
- Modify: `src/agent/OpenAgent.sln`

- [ ] **Step 1: Create the project directory**

```bash
mkdir -p src/agent/OpenAgent.ScheduledTasks/Models
mkdir -p src/agent/OpenAgent.ScheduledTasks/Storage
```

- [ ] **Step 2: Create the .csproj file**

Create `src/agent/OpenAgent.ScheduledTasks/OpenAgent.ScheduledTasks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
    <ProjectReference Include="..\OpenAgent.Models\OpenAgent.Models.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Cronos" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add Cronos to Directory.Packages.props**

In `src/agent/Directory.Packages.props`, add inside the `<ItemGroup>`:

```xml
<PackageReference Include="Cronos" Version="0.8.4" />
```

Check `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` — they may already be in Directory.Packages.props. If not, add them too.

- [ ] **Step 4: Add project to solution**

```bash
cd src/agent && dotnet sln add OpenAgent.ScheduledTasks/OpenAgent.ScheduledTasks.csproj
```

- [ ] **Step 5: Create the models file**

Create `src/agent/OpenAgent.ScheduledTasks/Models/ScheduledTask.cs`:

```csharp
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
```

- [ ] **Step 6: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: create OpenAgent.ScheduledTasks project with data models"
```

---

## Task 6: ScheduledTaskStore — JSON Persistence

**Files:**
- Create: `src/agent/OpenAgent.ScheduledTasks/Storage/ScheduledTaskStore.cs`

- [ ] **Step 1: Create the store**

Create `src/agent/OpenAgent.ScheduledTasks/Storage/ScheduledTaskStore.cs`:

```csharp
using System.Text.Json;
using OpenAgent.ScheduledTasks.Models;

namespace OpenAgent.ScheduledTasks.Storage;

/// <summary>
/// Loads and saves scheduled tasks to a JSON file. Maintains an in-memory cache.
/// All public methods are NOT thread-safe — callers must hold the service lock.
/// </summary>
internal sealed class ScheduledTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private List<ScheduledTask> _tasks = [];

    public ScheduledTaskStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>All tasks currently in memory.</summary>
    public IReadOnlyList<ScheduledTask> Tasks => _tasks;

    /// <summary>Loads tasks from disk into memory. Creates empty file if missing.</summary>
    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _tasks = [];
            return;
        }

        var json = File.ReadAllText(_filePath);
        var file = JsonSerializer.Deserialize<ScheduledTaskFile>(json, JsonOptions);
        _tasks = file?.Tasks ?? [];
    }

    /// <summary>Persists current in-memory state to disk.</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var file = new ScheduledTaskFile { Tasks = _tasks };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    /// <summary>Returns a task by ID, or null if not found.</summary>
    public ScheduledTask? Get(string taskId) =>
        _tasks.FirstOrDefault(t => t.Id == taskId);

    /// <summary>Adds a task to the in-memory list.</summary>
    public void Add(ScheduledTask task) =>
        _tasks.Add(task);

    /// <summary>Removes a task by ID. Returns true if found.</summary>
    public bool Remove(string taskId)
    {
        var task = Get(taskId);
        if (task is null) return false;
        _tasks.Remove(task);
        return true;
    }
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.ScheduledTasks/Storage/ScheduledTaskStore.cs
git commit -m "feat: add ScheduledTaskStore for JSON persistence"
```

---

## Task 7: ScheduleCalculator — Compute NextRunAt

**Files:**
- Create: `src/agent/OpenAgent.ScheduledTasks/ScheduleCalculator.cs`

- [ ] **Step 1: Create the calculator**

Create `src/agent/OpenAgent.ScheduledTasks/ScheduleCalculator.cs`:

```csharp
using Cronos;
using OpenAgent.ScheduledTasks.Models;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Computes the next run time for a scheduled task based on its schedule configuration.
/// </summary>
internal static class ScheduleCalculator
{
    /// <summary>
    /// Computes the next occurrence after <paramref name="after"/> based on the schedule.
    /// Returns null if the task will never run again (e.g. one-shot already past).
    /// </summary>
    public static DateTimeOffset? ComputeNextRun(ScheduleConfig schedule, DateTimeOffset after)
    {
        if (schedule.Cron is not null)
        {
            var expression = CronExpression.Parse(schedule.Cron);
            var tz = schedule.Timezone is not null
                ? TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone)
                : TimeZoneInfo.Utc;
            var next = expression.GetNextOccurrence(after, tz);
            return next.HasValue ? new DateTimeOffset(next.Value, TimeSpan.Zero) : null;
        }

        if (schedule.IntervalMs is > 0)
        {
            return after.AddMilliseconds(schedule.IntervalMs.Value);
        }

        if (schedule.At is not null)
        {
            // One-shot: only return if it's in the future
            return schedule.At.Value > after ? schedule.At.Value : null;
        }

        return null;
    }

    /// <summary>
    /// Validates that exactly one schedule type is set. Returns an error message or null if valid.
    /// </summary>
    public static string? Validate(ScheduleConfig schedule)
    {
        var count = 0;
        if (schedule.Cron is not null) count++;
        if (schedule.IntervalMs is not null) count++;
        if (schedule.At is not null) count++;

        return count switch
        {
            0 => "Schedule must specify exactly one of: cron, intervalMs, or at.",
            1 => ValidateIndividual(schedule),
            _ => "Schedule must specify exactly one of: cron, intervalMs, or at. Multiple were set."
        };
    }

    private static string? ValidateIndividual(ScheduleConfig schedule)
    {
        if (schedule.Cron is not null)
        {
            try { CronExpression.Parse(schedule.Cron); }
            catch (CronFormatException ex) { return $"Invalid cron expression: {ex.Message}"; }
        }

        if (schedule.IntervalMs is <= 0)
            return "intervalMs must be a positive number.";

        if (schedule.Timezone is not null)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone); }
            catch (TimeZoneNotFoundException) { return $"Unknown timezone: {schedule.Timezone}"; }
        }

        return null;
    }
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.ScheduledTasks/ScheduleCalculator.cs
git commit -m "feat: add ScheduleCalculator for cron/interval/one-shot next-run computation"
```

---

## Task 8: ScheduledTaskExecutor — Run a Single Task

**Files:**
- Create: `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskExecutor.cs`

- [ ] **Step 1: Create the executor**

Create `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskExecutor.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks.Models;
using System.Text;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Executes a single scheduled task: creates/retrieves a conversation, runs an LLM completion,
/// and returns the assistant response text.
/// </summary>
internal sealed class ScheduledTaskExecutor(
    IConversationStore conversationStore,
    Func<string, ILlmTextProvider> textProviderResolver,
    AgentConfig agentConfig,
    ILogger<ScheduledTaskExecutor> logger)
{
    /// <summary>
    /// Executes the task's prompt as a user message against a dedicated conversation.
    /// Returns the collected assistant response text.
    /// </summary>
    public async Task<string> ExecuteAsync(ScheduledTask task, string? promptOverride, CancellationToken ct)
    {
        var conversationId = $"scheduledtask:{task.Id}";
        var prompt = promptOverride ?? task.Prompt;

        // Get or create the dedicated conversation for this task
        var conversation = conversationStore.GetOrCreate(
            conversationId,
            "scheduledtask",
            ConversationType.ScheduledTask,
            agentConfig.TextProvider,
            agentConfig.TextModel);

        // Build the user message
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversationId,
            Role = "user",
            Content = prompt
        };

        // Resolve the text provider and run completion
        var provider = textProviderResolver(conversation.Provider);
        var responseBuilder = new StringBuilder();

        await foreach (var evt in provider.CompleteAsync(conversation, userMessage, ct))
        {
            if (evt is TextDelta delta)
                responseBuilder.Append(delta.Content);
        }

        var response = responseBuilder.ToString();
        logger.LogInformation("Scheduled task '{Name}' ({Id}) completed. Response length: {Length}",
            task.Name, task.Id, response.Length);

        return response;
    }
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.ScheduledTasks/ScheduledTaskExecutor.cs
git commit -m "feat: add ScheduledTaskExecutor for running individual tasks"
```

---

## Task 9: DeliveryRouter — Route Output to Targets

**Files:**
- Create: `src/agent/OpenAgent.ScheduledTasks/DeliveryRouter.cs`

- [ ] **Step 1: Create the router**

Create `src/agent/OpenAgent.ScheduledTasks/DeliveryRouter.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.ScheduledTasks.Models;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Routes completed task output to the configured delivery target.
/// </summary>
internal sealed class DeliveryRouter(
    IConnectionManager connectionManager,
    IHttpClientFactory httpClientFactory,
    ILogger<DeliveryRouter> logger)
{
    /// <summary>
    /// Delivers the task response based on the task's delivery configuration.
    /// </summary>
    public async Task DeliverAsync(ScheduledTask task, string response, CancellationToken ct)
    {
        var delivery = task.Delivery;
        if (delivery is null || delivery.Mode == DeliveryMode.Silent)
            return;

        switch (delivery.Mode)
        {
            case DeliveryMode.Channel:
                await DeliverToChannelAsync(task, delivery, response, ct);
                break;

            case DeliveryMode.Webhook:
                await DeliverToWebhookAsync(task, delivery, response, ct);
                break;
        }
    }

    private async Task DeliverToChannelAsync(
        ScheduledTask task, DeliveryConfig delivery, string response, CancellationToken ct)
    {
        if (delivery.ConnectionId is null || delivery.ChatId is null)
        {
            logger.LogWarning("Task '{Name}' ({Id}) has channel delivery but missing connectionId or chatId",
                task.Name, task.Id);
            return;
        }

        var provider = connectionManager.GetProvider(delivery.ConnectionId);
        if (provider is not IOutboundSender sender)
        {
            logger.LogWarning(
                "Task '{Name}' ({Id}): connection '{ConnectionId}' does not support outbound messaging. Falling back to silent.",
                task.Name, task.Id, delivery.ConnectionId);
            return;
        }

        await sender.SendMessageAsync(delivery.ChatId, response, ct);
        logger.LogInformation("Task '{Name}' ({Id}) delivered to channel {ConnectionId}:{ChatId}",
            task.Name, task.Id, delivery.ConnectionId, delivery.ChatId);
    }

    private async Task DeliverToWebhookAsync(
        ScheduledTask task, DeliveryConfig delivery, string response, CancellationToken ct)
    {
        if (delivery.WebhookUrl is null)
        {
            logger.LogWarning("Task '{Name}' ({Id}) has webhook delivery but missing webhookUrl", task.Name, task.Id);
            return;
        }

        var client = httpClientFactory.CreateClient("ScheduledTaskWebhook");
        client.Timeout = TimeSpan.FromSeconds(10);

        var payload = new WebhookPayload
        {
            TaskId = task.Id,
            TaskName = task.Name,
            Status = "success",
            Response = response,
            ExecutedAt = DateTimeOffset.UtcNow
        };

        await client.PostAsJsonAsync(delivery.WebhookUrl, payload, ct);
        logger.LogInformation("Task '{Name}' ({Id}) delivered to webhook {Url}", task.Name, task.Id, delivery.WebhookUrl);
    }

    internal sealed class WebhookPayload
    {
        [JsonPropertyName("taskId")]
        public required string TaskId { get; init; }

        [JsonPropertyName("taskName")]
        public required string TaskName { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("response")]
        public required string Response { get; init; }

        [JsonPropertyName("executedAt")]
        public required DateTimeOffset ExecutedAt { get; init; }
    }
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds. Note: `IHttpClientFactory` requires `Microsoft.Extensions.Http`. Check if the implicit framework reference covers it — if not, add `<PackageReference Include="Microsoft.Extensions.Http" />` to the .csproj.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.ScheduledTasks/DeliveryRouter.cs
git commit -m "feat: add DeliveryRouter for channel and webhook delivery"
```

---

## Task 10: ScheduledTaskService — The Engine

**Files:**
- Create: `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs`

- [ ] **Step 1: Create the service**

Create `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.ScheduledTasks.Models;
using OpenAgent.ScheduledTasks.Storage;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Hosted service that manages the scheduled task lifecycle: loading, scheduling,
/// executing, and persisting tasks. All mutations are serialized through a semaphore.
/// </summary>
public sealed class ScheduledTaskService : IHostedService, IDisposable
{
    private readonly ScheduledTaskStore _store;
    private readonly ScheduledTaskExecutor _executor;
    private readonly DeliveryRouter _deliveryRouter;
    private readonly ILogger<ScheduledTaskService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly int _maxConcurrentRuns;
    private Timer? _timer;

    public ScheduledTaskService(
        ScheduledTaskStore store,
        ScheduledTaskExecutor executor,
        DeliveryRouter deliveryRouter,
        ILogger<ScheduledTaskService> logger,
        int maxConcurrentRuns = 3)
    {
        _store = store;
        _executor = executor;
        _deliveryRouter = deliveryRouter;
        _logger = logger;
        _maxConcurrentRuns = maxConcurrentRuns;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _store.Load();
            RecomputeAllNextRuns();
            _store.Save();
            _logger.LogInformation("Scheduled task service started. {Count} tasks loaded.", _store.Tasks.Count);
        }
        finally { _lock.Release(); }

        // Run missed tasks (at most one catch-up per task)
        await RunMissedTasksAsync(ct);

        // Arm the timer — ticks every 30 seconds
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_timer is not null)
            await _timer.DisposeAsync();
        _timer = null;
        _logger.LogInformation("Scheduled task service stopped.");
    }

    public void Dispose() => _timer?.Dispose();

    // --- CRUD methods (called by tools and endpoints) ---

    /// <summary>Creates and persists a new scheduled task.</summary>
    public async Task<ScheduledTask> AddAsync(ScheduledTask task, CancellationToken ct = default)
    {
        var error = ScheduleCalculator.Validate(task.Schedule);
        if (error is not null)
            throw new ArgumentException(error);

        await _lock.WaitAsync(ct);
        try
        {
            task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, DateTimeOffset.UtcNow);
            _store.Add(task);
            _store.Save();
            _logger.LogInformation("Task '{Name}' ({Id}) added. Next run: {NextRun}", task.Name, task.Id, task.State.NextRunAt);
            return task;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Updates an existing task's mutable properties.</summary>
    public async Task<ScheduledTask> UpdateAsync(string taskId, Action<ScheduledTask> patch, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var task = _store.Get(taskId) ?? throw new KeyNotFoundException($"Task '{taskId}' not found.");
            patch(task);

            // Re-validate schedule if it was changed
            var error = ScheduleCalculator.Validate(task.Schedule);
            if (error is not null)
                throw new ArgumentException(error);

            task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, DateTimeOffset.UtcNow);
            _store.Save();
            return task;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Removes a task by ID.</summary>
    public async Task<bool> RemoveAsync(string taskId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var removed = _store.Remove(taskId);
            if (removed) _store.Save();
            return removed;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns all tasks.</summary>
    public async Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return _store.Tasks.ToList(); }
        finally { _lock.Release(); }
    }

    /// <summary>Returns a task by ID.</summary>
    public async Task<ScheduledTask?> GetAsync(string taskId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return _store.Get(taskId); }
        finally { _lock.Release(); }
    }

    /// <summary>Executes a task immediately, regardless of schedule.</summary>
    public async Task RunNowAsync(string taskId, string? promptOverride = null, CancellationToken ct = default)
    {
        ScheduledTask? task;
        await _lock.WaitAsync(ct);
        try { task = _store.Get(taskId) ?? throw new KeyNotFoundException($"Task '{taskId}' not found."); }
        finally { _lock.Release(); }

        await ExecuteTaskAsync(task, promptOverride, ct);
    }

    // --- Timer loop ---

    private async Task TickAsync()
    {
        try
        {
            List<ScheduledTask> dueTasks;
            await _lock.WaitAsync();
            try
            {
                var now = DateTimeOffset.UtcNow;
                dueTasks = _store.Tasks
                    .Where(t => t.Enabled && t.State.NextRunAt.HasValue && t.State.NextRunAt.Value <= now)
                    .Take(_maxConcurrentRuns)
                    .ToList();
            }
            finally { _lock.Release(); }

            if (dueTasks.Count == 0) return;

            _logger.LogInformation("Timer tick: {Count} due task(s) found.", dueTasks.Count);

            // Execute concurrently up to the cap
            var tasks = dueTasks.Select(t => ExecuteTaskAsync(t, promptOverride: null, CancellationToken.None));
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in scheduled task timer tick.");
        }
    }

    private async Task ExecuteTaskAsync(ScheduledTask task, string? promptOverride, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Executing task '{Name}' ({Id})...", task.Name, task.Id);
            var response = await _executor.ExecuteAsync(task, promptOverride, ct);
            await _deliveryRouter.DeliverAsync(task, response, ct);

            // Update state under lock
            await _lock.WaitAsync(ct);
            try
            {
                task.State.LastRunAt = DateTimeOffset.UtcNow;
                task.State.LastStatus = TaskRunStatus.Success;
                task.State.LastError = null;
                task.State.ConsecutiveErrors = 0;
                task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, DateTimeOffset.UtcNow);

                if (task.DeleteAfterRun)
                {
                    _store.Remove(task.Id);
                    _logger.LogInformation("One-shot task '{Name}' ({Id}) completed and removed.", task.Name, task.Id);
                }

                _store.Save();
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task '{Name}' ({Id}) failed.", task.Name, task.Id);

            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                task.State.LastRunAt = DateTimeOffset.UtcNow;
                task.State.LastStatus = TaskRunStatus.Error;
                task.State.LastError = ex.Message;
                task.State.ConsecutiveErrors++;
                task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, DateTimeOffset.UtcNow);
                _store.Save();
            }
            finally { _lock.Release(); }
        }
    }

    private async Task RunMissedTasksAsync(CancellationToken ct)
    {
        List<ScheduledTask> missedTasks;
        await _lock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            missedTasks = _store.Tasks
                .Where(t => t.Enabled && t.State.NextRunAt.HasValue && t.State.NextRunAt.Value < now)
                .ToList();
        }
        finally { _lock.Release(); }

        if (missedTasks.Count == 0) return;

        _logger.LogInformation("Running {Count} missed task(s) with stagger...", missedTasks.Count);
        foreach (var task in missedTasks)
        {
            await ExecuteTaskAsync(task, promptOverride: null, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private void RecomputeAllNextRuns()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var task in _store.Tasks.Where(t => t.Enabled))
        {
            task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, now);
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.ScheduledTasks/ScheduledTaskService.cs
git commit -m "feat: add ScheduledTaskService hosted service with timer loop and CRUD"
```

---

## Task 11: ScheduledTaskToolHandler — Agent Tools

**Files:**
- Create: `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskToolHandler.cs`

- [ ] **Step 1: Create the tool handler**

Create `src/agent/OpenAgent.ScheduledTasks/ScheduledTaskToolHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.ScheduledTasks.Models;
using System.Text.Json;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Provides agent tools for creating, listing, updating, and deleting scheduled tasks.
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

internal sealed class CreateScheduledTaskTool(ScheduledTaskService service, ILogger logger) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_scheduled_task",
        Description = "Create a new scheduled task that runs an LLM prompt on a schedule. The task can deliver results to a channel (Telegram/WhatsApp), a webhook URL, or just store them silently.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "Short name for the task" },
                prompt = new { type = "string", description = "The prompt to send to the agent when the task runs" },
                schedule = new
                {
                    type = "object",
                    description = "Schedule config. Set exactly one of: cron, intervalMs, or at.",
                    properties = new
                    {
                        cron = new { type = "string", description = "Cron expression, e.g. '0 9 * * *' for daily at 9am" },
                        timezone = new { type = "string", description = "IANA timezone for cron, e.g. 'Europe/Copenhagen'. Defaults to UTC." },
                        intervalMs = new { type = "integer", description = "Repeat every N milliseconds" },
                        at = new { type = "string", description = "ISO 8601 timestamp for one-shot execution" }
                    }
                },
                description = new { type = "string", description = "Optional longer description" },
                deleteAfterRun = new { type = "boolean", description = "If true, delete the task after it runs once (default false)" },
                delivery = new
                {
                    type = "object",
                    description = "Where to deliver results. Default is silent (conversation only).",
                    properties = new
                    {
                        mode = new { type = "string", description = "Delivery mode: 'silent', 'channel', or 'webhook'" },
                        connectionId = new { type = "string", description = "Channel connection ID for channel delivery" },
                        chatId = new { type = "string", description = "Chat ID on the channel" },
                        webhookUrl = new { type = "string", description = "URL for webhook delivery" }
                    }
                }
            },
            required = new[] { "name", "prompt", "schedule" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;

        var task = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString(),
            Name = args.GetProperty("name").GetString()!,
            Prompt = args.GetProperty("prompt").GetString()!,
            Schedule = JsonSerializer.Deserialize<ScheduleConfig>(args.GetProperty("schedule").GetRawText())!,
            Description = args.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            DeleteAfterRun = args.TryGetProperty("deleteAfterRun", out var dar) && dar.GetBoolean(),
            Delivery = args.TryGetProperty("delivery", out var del)
                ? JsonSerializer.Deserialize<DeliveryConfig>(del.GetRawText())
                : null
        };

        // Context-aware delivery defaults: if conversation is from a channel, use it
        if (task.Delivery?.Mode == DeliveryMode.Channel
            && task.Delivery.ConnectionId is null
            && TryParseChannelConversation(conversationId, out var connId, out var chatId))
        {
            task.Delivery.ConnectionId = connId;
            task.Delivery.ChatId = chatId;
        }

        try
        {
            var created = await service.AddAsync(task, ct);
            return JsonSerializer.Serialize(new
            {
                status = "created",
                id = created.Id,
                name = created.Name,
                nextRunAt = created.State.NextRunAt?.ToString("o")
            });
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    /// <summary>Parses "telegram:connId:chatId" or "whatsapp:connId:chatId" conversation IDs.</summary>
    private static bool TryParseChannelConversation(string conversationId, out string connectionId, out string chatId)
    {
        connectionId = chatId = "";
        var parts = conversationId.Split(':');
        if (parts.Length < 3) return false;
        if (parts[0] is not ("telegram" or "whatsapp")) return false;
        connectionId = parts[1];
        chatId = string.Join(":", parts.Skip(2)); // chatId may contain colons
        return true;
    }
}

internal sealed class ListScheduledTasksTool(ScheduledTaskService service) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_scheduled_tasks",
        Description = "List all scheduled tasks with their status, schedule, and next run time.",
        Parameters = new { type = "object", properties = new { } }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var tasks = await service.ListAsync(ct);
        var summaries = tasks.Select(t => new
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
        return JsonSerializer.Serialize(summaries);
    }

    private static string FormatSchedule(ScheduleConfig s)
    {
        if (s.Cron is not null) return $"cron: {s.Cron}" + (s.Timezone is not null ? $" ({s.Timezone})" : "");
        if (s.IntervalMs is not null) return $"every {s.IntervalMs}ms";
        if (s.At is not null) return $"at {s.At.Value:o}";
        return "unknown";
    }
}

internal sealed class UpdateScheduledTaskTool(ScheduledTaskService service, ILogger logger) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "update_scheduled_task",
        Description = "Update an existing scheduled task. Only provided fields are changed.",
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
                    description = "New schedule. Set exactly one of: cron, intervalMs, or at.",
                    properties = new
                    {
                        cron = new { type = "string" },
                        timezone = new { type = "string" },
                        intervalMs = new { type = "integer" },
                        at = new { type = "string" }
                    }
                },
                delivery = new
                {
                    type = "object",
                    properties = new
                    {
                        mode = new { type = "string" },
                        connectionId = new { type = "string" },
                        chatId = new { type = "string" },
                        webhookUrl = new { type = "string" }
                    }
                }
            },
            required = new[] { "taskId" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var taskId = args.GetProperty("taskId").GetString()!;

        try
        {
            var updated = await service.UpdateAsync(taskId, task =>
            {
                if (args.TryGetProperty("name", out var n)) task.Name = n.GetString()!;
                if (args.TryGetProperty("prompt", out var p)) task.Prompt = p.GetString()!;
                if (args.TryGetProperty("enabled", out var e)) task.Enabled = e.GetBoolean();
                if (args.TryGetProperty("schedule", out var s))
                    task.Schedule = JsonSerializer.Deserialize<ScheduleConfig>(s.GetRawText())!;
                if (args.TryGetProperty("delivery", out var d))
                    task.Delivery = JsonSerializer.Deserialize<DeliveryConfig>(d.GetRawText());
            }, ct);

            return JsonSerializer.Serialize(new { status = "updated", id = updated.Id, name = updated.Name });
        }
        catch (KeyNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }
}

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
        var args = JsonDocument.Parse(arguments).RootElement;
        var taskId = args.GetProperty("taskId").GetString()!;

        var removed = await service.RemoveAsync(taskId, ct);
        return JsonSerializer.Serialize(new
        {
            status = removed ? "deleted" : "not_found",
            taskId
        });
    }
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.ScheduledTasks/ScheduledTaskToolHandler.cs
git commit -m "feat: add agent tools for scheduled task CRUD"
```

---

## Task 12: ServiceCollectionExtensions — DI Wiring

**Files:**
- Create: `src/agent/OpenAgent.ScheduledTasks/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create the extension**

Create `src/agent/OpenAgent.ScheduledTasks/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
using OpenAgent.ScheduledTasks.Storage;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// DI registration for the scheduled tasks subsystem.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers ScheduledTaskService (hosted), ScheduledTaskExecutor, DeliveryRouter,
    /// ScheduledTaskToolHandler, and supporting infrastructure.
    /// </summary>
    public static IServiceCollection AddScheduledTasks(this IServiceCollection services, string dataPath)
    {
        var storePath = Path.Combine(dataPath, "config", "scheduled-tasks.json");

        services.AddSingleton(new ScheduledTaskStore(storePath));
        services.AddSingleton<ScheduledTaskExecutor>();
        services.AddSingleton<DeliveryRouter>();
        services.AddSingleton<ScheduledTaskService>();
        services.AddHostedService(sp => sp.GetRequiredService<ScheduledTaskService>());
        services.AddSingleton<IToolHandler, ScheduledTaskToolHandler>();
        services.AddHttpClient("ScheduledTaskWebhook");

        return services;
    }
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.ScheduledTasks/ServiceCollectionExtensions.cs
git commit -m "feat: add DI registration for scheduled tasks"
```

---

## Task 13: Wire Into Host Project

**Files:**
- Modify: `src/agent/OpenAgent/OpenAgent.csproj`
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Add project reference to host**

In `src/agent/OpenAgent/OpenAgent.csproj`, add inside the existing `<ItemGroup>` with project references:

```xml
<ProjectReference Include="..\OpenAgent.ScheduledTasks\OpenAgent.ScheduledTasks.csproj" />
```

- [ ] **Step 2: Add DI registration in Program.cs**

In `src/agent/OpenAgent/Program.cs`, after the existing tool handler registrations (around line 70), add:

```csharp
builder.Services.AddScheduledTasks(dataPath);
```

Add the using at the top of the file:

```csharp
using OpenAgent.ScheduledTasks;
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent/OpenAgent.csproj src/agent/OpenAgent/Program.cs
git commit -m "feat: wire scheduled tasks into host DI and startup"
```

---

## Task 14: REST API Endpoints

**Files:**
- Create: `src/agent/OpenAgent.Api/Endpoints/ScheduledTaskEndpoints.cs`
- Modify: `src/agent/OpenAgent.Api/OpenAgent.Api.csproj`
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Add project reference to Api**

In `src/agent/OpenAgent.Api/OpenAgent.Api.csproj`, add:

```xml
<ProjectReference Include="..\OpenAgent.ScheduledTasks\OpenAgent.ScheduledTasks.csproj" />
```

- [ ] **Step 2: Create the endpoints**

Create `src/agent/OpenAgent.Api/Endpoints/ScheduledTaskEndpoints.cs`:

```csharp
using OpenAgent.ScheduledTasks;
using OpenAgent.ScheduledTasks.Models;
using System.Text.Json;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// REST endpoints for scheduled task CRUD and execution.
/// </summary>
public static class ScheduledTaskEndpoints
{
    public static void MapScheduledTaskEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/scheduled-tasks").RequireAuthorization();

        // List all tasks
        group.MapGet("/", async (ScheduledTaskService service, CancellationToken ct) =>
        {
            var tasks = await service.ListAsync(ct);
            return Results.Ok(tasks);
        });

        // Get single task
        group.MapGet("/{taskId}", async (string taskId, ScheduledTaskService service, CancellationToken ct) =>
        {
            var task = await service.GetAsync(taskId, ct);
            return task is not null ? Results.Ok(task) : Results.NotFound();
        });

        // Create task
        group.MapPost("/", async (ScheduledTask task, ScheduledTaskService service, CancellationToken ct) =>
        {
            try
            {
                var created = await service.AddAsync(task, ct);
                return Results.Created($"/api/scheduled-tasks/{created.Id}", created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Update task
        group.MapPut("/{taskId}", async (string taskId, JsonElement body, ScheduledTaskService service, CancellationToken ct) =>
        {
            try
            {
                var updated = await service.UpdateAsync(taskId, task =>
                {
                    if (body.TryGetProperty("name", out var n)) task.Name = n.GetString()!;
                    if (body.TryGetProperty("prompt", out var p)) task.Prompt = p.GetString()!;
                    if (body.TryGetProperty("enabled", out var e)) task.Enabled = e.GetBoolean();
                    if (body.TryGetProperty("description", out var d)) task.Description = d.GetString();
                    if (body.TryGetProperty("schedule", out var s))
                        task.Schedule = JsonSerializer.Deserialize<ScheduleConfig>(s.GetRawText())!;
                    if (body.TryGetProperty("delivery", out var del))
                        task.Delivery = JsonSerializer.Deserialize<DeliveryConfig>(del.GetRawText());
                }, ct);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Delete task
        group.MapDelete("/{taskId}", async (string taskId, ScheduledTaskService service, CancellationToken ct) =>
        {
            var removed = await service.RemoveAsync(taskId, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // Execute immediately
        group.MapPost("/{taskId}/run", async (string taskId, ScheduledTaskService service, CancellationToken ct) =>
        {
            try
            {
                await service.RunNowAsync(taskId, ct: ct);
                return Results.Ok(new { status = "executed", taskId });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        // Webhook trigger
        group.MapPost("/{taskId}/trigger", async (string taskId, HttpRequest request, ScheduledTaskService service, CancellationToken ct) =>
        {
            try
            {
                // Read optional webhook body
                string? promptOverride = null;
                if (request.ContentLength > 0)
                {
                    var task = await service.GetAsync(taskId, ct);
                    if (task is null) return Results.NotFound();

                    using var reader = new StreamReader(request.Body);
                    var body = await reader.ReadToEndAsync(ct);
                    promptOverride = $"{task.Prompt}\n\n<webhook_context>\n{body}\n</webhook_context>";
                }

                await service.RunNowAsync(taskId, promptOverride, ct);
                return Results.Ok(new { status = "triggered", taskId });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });
    }
}
```

- [ ] **Step 3: Map endpoints in Program.cs**

In `src/agent/OpenAgent/Program.cs`, after the existing endpoint mapping calls (around line 164), add:

```csharp
app.MapScheduledTaskEndpoints();
```

Add the using if not already present:

```csharp
using OpenAgent.Api.Endpoints;
```

(Check if this namespace is already imported — it likely is since other endpoints use it.)

- [ ] **Step 4: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add REST endpoints for scheduled task CRUD and webhook trigger"
```

---

## Task 15: Integration Tests

**Files:**
- Modify: `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj`
- Create: `src/agent/OpenAgent.Tests/ScheduledTaskEndpointTests.cs`

- [ ] **Step 1: Add project reference to tests**

In `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj`, add:

```xml
<ProjectReference Include="..\OpenAgent.ScheduledTasks\OpenAgent.ScheduledTasks.csproj" />
```

- [ ] **Step 2: Create endpoint integration tests**

Create `src/agent/OpenAgent.Tests/ScheduledTaskEndpointTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts;
using OpenAgent.ScheduledTasks.Models;
using OpenAgent.Tests.Fakes;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenAgent.Tests;

public class ScheduledTaskEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ScheduledTaskEndpointTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace text provider with fake
                services.RemoveAll(typeof(ILlmTextProvider));
                var fake = new FakeTextProvider();
                services.AddKeyedSingleton<ILlmTextProvider>("azure-openai-text", fake);
                services.AddSingleton<ILlmTextProvider>(fake);
            });
        });
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");
        return client;
    }

    [Fact]
    public async Task ListTasks_Empty_ReturnsEmptyArray()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/scheduled-tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, tasks.ValueKind);
    }

    [Fact]
    public async Task CreateTask_ValidCron_ReturnsCreated()
    {
        var client = CreateClient();
        var task = new
        {
            id = Guid.NewGuid().ToString(),
            name = "Test cron task",
            prompt = "Say hello",
            schedule = new { cron = "0 9 * * *" }
        };

        var response = await client.PostAsJsonAsync("/api/scheduled-tasks", task);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(task.name, created.GetProperty("name").GetString());
        Assert.True(created.GetProperty("state").GetProperty("nextRunAt").GetString() is not null);
    }

    [Fact]
    public async Task CreateTask_InvalidSchedule_ReturnsBadRequest()
    {
        var client = CreateClient();
        var task = new
        {
            id = Guid.NewGuid().ToString(),
            name = "Bad task",
            prompt = "Say hello",
            schedule = new { } // No schedule type set
        };

        var response = await client.PostAsJsonAsync("/api/scheduled-tasks", task);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetTask_NotFound_Returns404()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/scheduled-tasks/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteTask_Exists_ReturnsNoContent()
    {
        var client = CreateClient();
        var taskId = Guid.NewGuid().ToString();

        // Create first
        await client.PostAsJsonAsync("/api/scheduled-tasks", new
        {
            id = taskId,
            name = "To delete",
            prompt = "Hello",
            schedule = new { intervalMs = 60000 }
        });

        // Delete
        var response = await client.DeleteAsync($"/api/scheduled-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify gone
        var getResponse = await client.GetAsync($"/api/scheduled-tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task CreateTask_OneShot_ReturnsCreated()
    {
        var client = CreateClient();
        var task = new
        {
            id = Guid.NewGuid().ToString(),
            name = "One-shot reminder",
            prompt = "Remind me",
            schedule = new { at = DateTimeOffset.UtcNow.AddHours(1).ToString("o") },
            deleteAfterRun = true
        };

        var response = await client.PostAsJsonAsync("/api/scheduled-tasks", task);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Trigger_WithBody_ReturnsOk()
    {
        var client = CreateClient();
        var taskId = Guid.NewGuid().ToString();

        // Create task
        await client.PostAsJsonAsync("/api/scheduled-tasks", new
        {
            id = taskId,
            name = "Webhook task",
            prompt = "Process this event",
            schedule = new { intervalMs = 3600000 }
        });

        // Trigger with webhook context
        var triggerResponse = await client.PostAsJsonAsync(
            $"/api/scheduled-tasks/{taskId}/trigger",
            new { @event = "deploy.completed", data = new { env = "prod" } });

        Assert.Equal(HttpStatusCode.OK, triggerResponse.StatusCode);
    }
}
```

- [ ] **Step 3: Check FakeTextProvider exists**

The test depends on `FakeTextProvider` in the Fakes directory. Check that `src/agent/OpenAgent.Tests/Fakes/` has a text provider fake that returns some canned response. If the existing one is named differently (e.g. `FakeTelegramTextProvider` or `StreamingTextProvider`), use that instead, or adjust the keyed registration to match.

- [ ] **Step 4: Run tests**

Run: `cd src/agent && dotnet test`
Expected: All tests pass (both new and existing).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "test: add integration tests for scheduled task endpoints"
```

---

## Task 16: Build, Test, and Final Verification

- [ ] **Step 1: Full build**

Run: `cd src/agent && dotnet build`
Expected: Clean build, no warnings related to our changes.

- [ ] **Step 2: Full test suite**

Run: `cd src/agent && dotnet test`
Expected: All tests pass.

- [ ] **Step 3: Verify JSON storage works**

Manually check that the `scheduled-tasks.json` location in `{dataPath}/config/` is correctly resolved. The file should be created on first task creation, not on startup.

- [ ] **Step 4: Push feature branch**

```bash
git push -u origin feature/scheduled-tasks
```

---

## Task Dependency Order

```
Task 1 (rename enum) — no dependencies, do first
Task 2 (IOutboundSender) — no dependencies
Task 3 (Telegram IOutboundSender) — depends on Task 2
Task 4 (WhatsApp IOutboundSender) — depends on Task 2
Task 5 (project + models) — no dependencies
Task 6 (store) — depends on Task 5
Task 7 (schedule calculator) — depends on Task 5
Task 8 (executor) — depends on Tasks 1, 5
Task 9 (delivery router) — depends on Tasks 2, 5
Task 10 (service) — depends on Tasks 6, 7, 8, 9
Task 11 (tools) — depends on Task 10
Task 12 (DI extension) — depends on Tasks 10, 11
Task 13 (host wiring) — depends on Task 12
Task 14 (endpoints) — depends on Tasks 10, 13
Task 15 (tests) — depends on Task 14
Task 16 (verification) — depends on all
```

Parallelizable groups:
- **Group A:** Tasks 1, 2, 5 (independent foundations)
- **Group B:** Tasks 3, 4 (both depend on 2)
- **Group C:** Tasks 6, 7 (both depend on 5)
- **Group D:** Tasks 8, 9 (depend on different earlier tasks)
- **Sequential from here:** 10 → 11 → 12 → 13 → 14 → 15 → 16
