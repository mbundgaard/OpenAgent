# Background Heartbeat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the background agent from a separate agent with its own conversation into a heartbeat that nudges the *main* conversation, so it can see what it said and what Martin answered.

**Architecture:** Every 30 minutes (main quiet 15+ min) `BackgroundAgentRunner` injects an ephemeral user message into the conversation named by `AgentConfig.MainConversationId`. The agent takes a normal turn — full history, all tools, its real system prompt. It either replies (persisted by the provider, delivered via `DeliveryRouter`) or emits `[]` (provider discards the whole turn). The runner deletes the nudge afterwards in a `finally`, so the trigger never survives in the thread.

**Tech Stack:** .NET 10, xUnit, existing `IConversationStore` / `ILlmTextProvider` / `DeliveryRouter` abstractions.

## Global Constraints

- No emojis in code or comments.
- XML doc comments on public classes and their public methods.
- `[JsonPropertyName]` on all serialized models; never anonymous types for API payloads.
- Explicit variable names — `conversationId`, never bare `id`.
- Build: `cd src/agent && dotnet build`. Test: `cd src/agent && dotnet test`.
- **The `WebApplicationFactory` integration tests are flaky** (known `ObjectDisposedException` teardown race, documented in CLAUDE.md). Two to eleven failures per run in *varying* classes (`HealthEndpointTests`, `ChatEndpointTests`, `TelnyxWebhookEndpointTests`, ...) are **pre-existing, not a regression**. Only treat a failure as yours if it is in a class you touched and it fails *consistently* across runs.
- Production currently runs with `backgroundAgentEnabled=false`. It stays off until Task 7.

## Context: why this exists

On 2026-07-13 the background agent posted six near-identical messages, re-asking the same three questions. Martin *answered* them at 06:41 and the agent replied "that background message was wrong" — then re-asked at 07:09, 08:57 and 10:30 anyway. It lived in `system:background-agent`, had no tool that reads a conversation, and the `[]` sentinel discarded its turns, so it could see neither its own posts nor Martin's replies.

Full diagnosis and design: [docs/superpowers/specs/2026-07-13-background-heartbeat-redesign.md](../specs/2026-07-13-background-heartbeat-redesign.md).

**Verified precondition (do not re-litigate):** dropping the nudge leaves consecutive `assistant` messages in main. This is safe — production already contains three in a row (08:41, 08:57, 10:30) and a user turn at 13:44 ran a successful Anthropic completion against that history. No role-alternation guard is needed.

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/agent/OpenAgent.ScheduledTasks/SystemJobs/SystemJobRunner.cs` | Drives system jobs on a tick loop | Modify (window leak) |
| `src/agent/OpenAgent.BackgroundAgent/BackgroundAgentRunner.cs` | Gates + runs the heartbeat turn | Rewrite core |
| `src/agent/OpenAgent.BackgroundAgent/BackgroundAgentEndpoints.cs` | State + manual trigger | Modify |
| `src/agent/OpenAgent.BackgroundAgent/ServiceCollectionExtensions.cs` | DI | Modify |
| `src/agent/OpenAgent.BackgroundAgent/PostToMainTool.cs` | Outbound tool | **Delete** |
| `src/agent/OpenAgent.BackgroundAgent/BackgroundToolHandler.cs` | Tool grouping | **Delete** |
| `src/agent/OpenAgent/SystemPromptBuilder.cs` | Composes system prompt | Modify (drop `BackgroundOnly`) |
| `src/agent/OpenAgent/defaults/BACKGROUND.md` | Heartbeat instructions | Rewrite |
| `src/agent/OpenAgent/defaults/AGENTS.md` | Operating doc | Modify (daily-log trigger) |
| `src/agent/OpenAgent.LlmText.*/` (3 providers) | Turn execution | Modify (usage on suppressed turns) |
| `src/agent/OpenAgent.Tests/Fakes/NoopConnectionManager.cs` | Test fake | Create (lift from deleted file) |
| `src/agent/OpenAgent.Tests/Fakes/NoopWebSocketRegistry.cs` | Test fake | Create (lift from deleted file) |
| `src/agent/OpenAgent.Tests/Fakes/PersistingTextProvider.cs` | Test fake | Create |
| `src/agent/OpenAgent.Tests/PostToMainToolTests.cs` | Tool tests | **Delete** |

---

### Task 1: Fix the cron window leak

`SystemJobRunner.ExecuteAsync` returns early when a job is gated out, without advancing `NextRunAt`. A stale past `NextRunAt` therefore leaves the job permanently "due", so it fires the moment the interval gate opens — regardless of the hour. Observed firing at 22:54 and 22:30 CPH, outside the `*/15 6-21` window.

The existing comment warns that updating `LastRunAt` would reset interval gates. That is true, and we do not touch `LastRunAt`. `BackgroundAgentRunner`'s interval gate reads `jobState.LastRunAt`, **not** `NextRunAt` — so advancing `NextRunAt` on a gated-out tick is safe and fixes the leak.

**Files:**
- Modify: `src/agent/OpenAgent.ScheduledTasks/SystemJobs/SystemJobRunner.cs:129-133`
- Test: `src/agent/OpenAgent.Tests/SystemJobRunnerTests.cs`

**Interfaces:**
- Consumes: `ISystemJob` (`Name`, `Cron`, `Timezone`, `ShouldRunAsync`, `RunAsync`), `SystemJobStateStore.GetOrCreate(name)`, `ScheduleCalculator.ComputeNextRun(ScheduleConfig, DateTimeOffset)`.
- Produces: no new public API. `SystemJobRunner.ExecuteAsync` keeps its signature.

- [ ] **Step 1: Write the failing test**

Add to `src/agent/OpenAgent.Tests/SystemJobRunnerTests.cs`:

```csharp
    // Regression: a gated-out tick used to leave NextRunAt in the past, so the job stayed
    // permanently "due" and fired as soon as its interval gate opened - outside the cron
    // window. Observed in production at 22:54 CPH against a "6-21" cron.
    [Fact]
    public async Task Gated_out_tick_advances_next_run_to_the_next_cron_slot()
    {
        var store = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        store.Load();
        var job = new GatedJob();
        var runner = new SystemJobRunner([job], store, NullLogger<SystemJobRunner>.Instance);

        var state = store.GetOrCreate(job.Name);
        var stale = DateTimeOffset.UtcNow.AddHours(-3);
        state.NextRunAt = stale;

        await runner.ExecuteAsync(job, CancellationToken.None);

        Assert.False(job.Ran);
        Assert.NotNull(state.NextRunAt);
        Assert.True(state.NextRunAt > DateTimeOffset.UtcNow,
            $"gated-out tick must push NextRunAt into the future, was {state.NextRunAt}");
    }

    // A gated-out tick must NOT touch LastRunAt - the interval gates in BackgroundAgentRunner
    // are computed from it, and resetting it would starve them forever.
    [Fact]
    public async Task Gated_out_tick_does_not_touch_last_run_at()
    {
        var store = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        store.Load();
        var job = new GatedJob();
        var runner = new SystemJobRunner([job], store, NullLogger<SystemJobRunner>.Instance);

        var state = store.GetOrCreate(job.Name);
        var lastRun = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.LastRunAt = lastRun;
        state.NextRunAt = DateTimeOffset.UtcNow.AddHours(-1);

        await runner.ExecuteAsync(job, CancellationToken.None);

        Assert.Equal(lastRun, state.LastRunAt);
    }

    private sealed class GatedJob : ISystemJob
    {
        public bool Ran { get; private set; }
        public string Name => "gated-job";
        public string Cron => "*/15 6-21 * * *";
        public string Timezone => "Europe/Copenhagen";
        public Task<bool> ShouldRunAsync(CancellationToken ct) => Task.FromResult(false);
        public Task RunAsync(CancellationToken ct) { Ran = true; return Task.CompletedTask; }
    }
```

If `SystemJobRunnerTests` has no `_dataPath` field or `NullLogger` import, add them at the top of the class:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
```

```csharp
    private readonly string _dataPath = Path.Combine(
        Path.GetTempPath(), "openagent-sysjob-" + Guid.NewGuid().ToString("N"));
```

and ensure the constructor calls `Directory.CreateDirectory(_dataPath);`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~SystemJobRunnerTests"`
Expected: FAIL — `Gated_out_tick_advances_next_run_to_the_next_cron_slot` asserts `state.NextRunAt > now` but it is still the 3-hour-old stale value.

- [ ] **Step 3: Implement the fix**

In `SystemJobRunner.ExecuteAsync`, replace the gated-out early return:

```csharp
        if (!shouldRun)
        {
            _logger.LogDebug("System job '{Name}' gated out — skipping this tick", job.Name);
            return;
        }
```

with:

```csharp
        if (!shouldRun)
        {
            // Advance NextRunAt to the next cron slot. Without this a stale past NextRunAt keeps
            // the job permanently due, so it fires the moment its interval gate opens - even
            // outside the cron window (observed firing at 22:54 CPH against a "6-21" cron).
            // LastRunAt is deliberately untouched: the interval gates are computed from it.
            lock (_lock)
            {
                var state = _store.GetOrCreate(job.Name);
                state.NextRunAt = ComputeNext(job, DateTimeOffset.UtcNow);
                _store.Save();
            }

            _logger.LogDebug("System job '{Name}' gated out — skipping this tick", job.Name);
            return;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~SystemJobRunnerTests|FullyQualifiedName~BackgroundAgentRunnerTests"`
Expected: PASS, all green.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.ScheduledTasks/SystemJobs/SystemJobRunner.cs src/agent/OpenAgent.Tests/SystemJobRunnerTests.cs
git commit -m "fix(system-jobs): advance NextRunAt on gated-out ticks so the cron window holds"
```

---

### Task 2: Record token usage on suppressed turns

The `[]` sentinel `yield break`s *before* the usage-stats block, so a discarded turn logs no tokens and updates no totals. `system:background-agent` reported `turns 0, prompt_tokens 0` despite ~16 real runs doing 25-60s of tool work each. The heartbeat costs real money and reports zero.

We add token totals and a log line to the suppressed path. We deliberately do **not** touch `TurnCount`, `LastActivity`, or `LastPromptTokens`: no turn was recorded, the conversation did not become active, and `LastPromptTokens` drives the compaction threshold — feeding it a figure from a turn whose messages were just deleted would trigger spurious compaction.

**Honest note on testing:** these three providers have **no HTTP test harness** in the repo — there are no unit tests for their completion loops at all. Do not invent one for this fix; that is a larger piece of work than the fix itself. This is a code move, verified by the build and by observing production logs in Task 7.

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:388-398`
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:325-335`
- Modify: `src/agent/OpenAgent.LlmText.OpenAISubscription/OpenAiSubscriptionTextProvider.cs:537`

**Interfaces:**
- Consumes: `IAgentLogic.GetConversation`, `IAgentLogic.UpdateConversation`, `IAgentLogic.DeleteMessages`, `ResponseSuppression.IsSuppressed`.
- Produces: no new public API.

- [ ] **Step 1: Anthropic provider — record usage before discarding**

In `AnthropicSubscriptionTextProvider.CompleteAsync`, replace the suppressed block:

```csharp
            if (ResponseSuppression.IsSuppressed(fullContent.ToString()))
            {
                agentLogic.DeleteMessages(conversationId, turnMessageIds);
                logger.LogInformation(
                    "Conversation {ConversationId}: agent emitted [] sentinel — turn discarded ({Count} message(s) removed)",
                    conversationId, turnMessageIds.Count);
                if (toolCallsStarted)
                    yield return new ThinkingStopped();
                yield return new ResponseSuppressed();
                yield break;
            }
```

with:

```csharp
            if (ResponseSuppression.IsSuppressed(fullContent.ToString()))
            {
                agentLogic.DeleteMessages(conversationId, turnMessageIds);

                // A discarded turn still cost tokens. Roll them into the conversation totals so
                // silent heartbeat runs are not invisible in cost accounting. TurnCount,
                // LastActivity and LastPromptTokens are deliberately NOT updated: no turn was
                // recorded, the conversation did not become active, and LastPromptTokens drives
                // the compaction threshold - feeding it a figure from a turn whose messages were
                // just deleted would trigger spurious compaction.
                var suppressed = agentLogic.GetConversation(conversationId) ?? conversation;
                suppressed.TotalPromptTokens += inputTokens ?? 0;
                suppressed.TotalCompletionTokens += outputTokens ?? 0;
                agentLogic.UpdateConversation(suppressed);

                logger.LogInformation(
                    "Conversation {ConversationId}: agent emitted [] sentinel — turn discarded ({Count} message(s) removed), {InputTokens} input, {OutputTokens} output tokens, {ElapsedMs}ms",
                    conversationId, turnMessageIds.Count, inputTokens, outputTokens, stopwatch.ElapsedMilliseconds);

                if (toolCallsStarted)
                    yield return new ThinkingStopped();
                yield return new ResponseSuppressed();
                yield break;
            }
```

- [ ] **Step 2: Azure provider — same fix**

In `AzureOpenAiTextProvider.CompleteAsync`, replace the suppressed block with (note the local token variables are named `promptTokens` / `completionTokens` here):

```csharp
            if (ResponseSuppression.IsSuppressed(fullContent.ToString()))
            {
                agentLogic.DeleteMessages(conversationId, turnMessageIds);

                // A discarded turn still cost tokens. Roll them into the conversation totals so
                // silent heartbeat runs are not invisible in cost accounting. TurnCount,
                // LastActivity and LastPromptTokens are deliberately NOT updated - see the
                // Anthropic provider for the reasoning.
                var suppressed = agentLogic.GetConversation(conversationId) ?? conversation;
                suppressed.TotalPromptTokens += promptTokens ?? 0;
                suppressed.TotalCompletionTokens += completionTokens ?? 0;
                agentLogic.UpdateConversation(suppressed);

                logger.LogInformation(
                    "Conversation {ConversationId}: agent emitted [] sentinel — turn discarded ({Count} message(s) removed), {PromptTokens} prompt, {CompletionTokens} completion tokens, {ElapsedMs}ms",
                    conversationId, turnMessageIds.Count, promptTokens, completionTokens, stopwatch.ElapsedMilliseconds);

                if (toolCallsStarted)
                    yield return new ThinkingStopped();
                yield return new ResponseSuppressed();
                yield break;
            }
```

- [ ] **Step 3: OpenAI subscription provider — same fix**

In `OpenAiSubscriptionTextProvider.CompleteAsync` (around line 537), replace the suppressed block. Note this provider has no `toolCallsStarted` guard and does not emit `ThinkingStopped` here — do not add one:

```csharp
            if (ResponseSuppression.IsSuppressed(text.ToString()))
            {
                agentLogic.DeleteMessages(conversationId, turnMessageIds);

                // A discarded turn still cost tokens. Roll them into the conversation totals so
                // silent heartbeat runs are not invisible in cost accounting. TurnCount,
                // LastActivity and LastPromptTokens are deliberately NOT updated - see the
                // Anthropic provider for the reasoning.
                var suppressed = agentLogic.GetConversation(conversationId) ?? conversation;
                suppressed.TotalPromptTokens += promptTokens ?? 0;
                suppressed.TotalCompletionTokens += completionTokens ?? 0;
                agentLogic.UpdateConversation(suppressed);

                logger.LogInformation(
                    "Conversation {ConversationId}: agent emitted [] sentinel — turn discarded ({Count} message(s) removed), {PromptTokens} prompt, {CompletionTokens} completion tokens, {ElapsedMs}ms",
                    conversationId, turnMessageIds.Count, promptTokens, completionTokens, stopwatch.ElapsedMilliseconds);

                yield return new ResponseSuppressed();
                yield break;
            }
```

- [ ] **Step 4: Build and run the full suite**

Run: `cd src/agent && dotnet build`
Expected: `0 Error(s)`.

Run: `cd src/agent && dotnet test`
Expected: all non-flaky tests pass. Ignore `WebApplicationFactory` teardown failures per the Global Constraints.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.LlmText.AnthropicSubscription src/agent/OpenAgent.LlmText.OpenAIAzure src/agent/OpenAgent.LlmText.OpenAISubscription
git commit -m "fix(providers): record token usage on suppressed turns instead of reporting zero"
```

---

### Task 3: Lift the test fakes out of the file we are about to delete

`NoopConnectionManager` and `NoopWebSocketRegistry` are private nested classes inside `PostToMainToolTests.cs`, which Task 5 deletes. `BackgroundAgentRunnerTests` needs them to construct a `DeliveryRouter`. Lift them into `Fakes/` first, and add a fake provider that persists like the real ones do — `StreamingTextProvider` writes nothing to the store, so a "the nudge is not persisted" assertion against it would be vacuously true.

**Files:**
- Create: `src/agent/OpenAgent.Tests/Fakes/NoopConnectionManager.cs`
- Create: `src/agent/OpenAgent.Tests/Fakes/NoopWebSocketRegistry.cs`
- Create: `src/agent/OpenAgent.Tests/Fakes/PersistingTextProvider.cs`

**Interfaces:**
- Consumes: `IConnectionManager`, `IWebSocketRegistry`, `ILlmTextProvider`, `IConversationStore`.
- Produces: `NoopConnectionManager()`, `NoopWebSocketRegistry()`, and
  `PersistingTextProvider(IConversationStore store, string reply)` with a
  `public List<string> PersistedUserContents { get; }` used by Task 4's tests.

- [ ] **Step 1: Create NoopConnectionManager**

```csharp
using OpenAgent.Contracts;

namespace OpenAgent.Tests.Fakes;

/// <summary>Connection manager that reports nothing running. Lets tests construct a DeliveryRouter.</summary>
public sealed class NoopConnectionManager : IConnectionManager
{
    public bool IsRunning(string connectionId) => false;
    public IChannelProvider? GetProvider(string connectionId) => null;
    public Task StartConnectionAsync(string connectionId, CancellationToken ct) => Task.CompletedTask;
    public Task StopConnectionAsync(string connectionId, CancellationToken ct) => Task.CompletedTask;
    public IEnumerable<(string ConnectionId, IChannelProvider Provider)> GetProviders() => [];
}
```

- [ ] **Step 2: Create NoopWebSocketRegistry**

```csharp
using System.Net.WebSockets;
using OpenAgent.Contracts;

namespace OpenAgent.Tests.Fakes;

/// <summary>WebSocket registry with no live sockets. Lets tests construct a DeliveryRouter.</summary>
public sealed class NoopWebSocketRegistry : IWebSocketRegistry
{
    public void Register(string conversationId, WebSocket webSocket) { }
    public void Unregister(string conversationId, WebSocket webSocket) { }
    public WebSocket? Get(string conversationId) => null;
}
```

- [ ] **Step 3: Create PersistingTextProvider**

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Text provider that mimics what the real providers do to the store: it persists the incoming
/// user message, then persists an assistant reply. StreamingTextProvider writes nothing, which
/// would make "the nudge is not persisted" assertions pass vacuously.
///
/// When the configured reply is the "[]" sentinel it mimics suppression instead: the whole turn
/// (user message included) is deleted and ResponseSuppressed is emitted.
/// </summary>
public sealed class PersistingTextProvider : ILlmTextProvider
{
    private readonly IConversationStore _store;
    private readonly string _reply;

    public PersistingTextProvider(IConversationStore store, string reply)
    {
        _store = store;
        _reply = reply;
    }

    /// <summary>Content of every user message this provider was asked to complete.</summary>
    public List<string> PersistedUserContents { get; } = [];

    public string Key => "persisting-text";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation,
        Message userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        PersistedUserContents.Add(userMessage.Content ?? "");
        _store.AddMessage(conversation.Id, userMessage);

        yield return new TextDelta(_reply);
        await Task.Yield();

        if (ResponseSuppression.IsSuppressed(_reply))
        {
            // Mirror the real providers: the sentinel discards the entire turn.
            _store.DeleteMessages(conversation.Id, [userMessage.Id]);
            yield return new ResponseSuppressed();
            yield break;
        }

        _store.AddMessage(conversation.Id, new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = _reply,
            Modality = MessageModality.Text
        });
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        IReadOnlyList<Message> messages,
        string model,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextDelta(_reply);
        await Task.Yield();
    }
}
```

- [ ] **Step 4: Build**

Run: `cd src/agent && dotnet build`
Expected: `0 Error(s)`. `PostToMainToolTests` still compiles — its own private nested copies of the Noop classes are still there and do not clash (different namespace scope).

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Tests/Fakes
git commit -m "test: add PersistingTextProvider and lift Noop fakes into Fakes/"
```

---

### Task 4: Rewrite BackgroundAgentRunner to nudge the main conversation

The core change. `RunAsync` stops creating `system:background-agent` and instead runs a turn in `AgentConfig.MainConversationId`, deleting the nudge afterwards and delivering any reply.

`ShouldRunAsync` and its gates (`MinSinceLastRun` = 30 min, `MinSinceLastMainMessage` = 15 min) are **unchanged**.

**Files:**
- Modify: `src/agent/OpenAgent.BackgroundAgent/BackgroundAgentRunner.cs`
- Test: `src/agent/OpenAgent.Tests/BackgroundAgentRunnerTests.cs`

**Interfaces:**
- Consumes: `IConversationStore.Get/AddMessage/DeleteMessages/GetMessages`, `Func<string, ILlmTextProvider>`, `AgentEnvironment.DataPath`, `AgentConfig.MainConversationId/TextProvider/TextModel`, `SystemJobStateStore`, `DeliveryRouter.DeliverAsync(Conversation, string, CancellationToken)`, `ResponseSuppression.IsSuppressed`.
- Produces: `BackgroundAgentRunner.JobName` (unchanged, `"background-agent"`), `ShouldRunAsync(DateTimeOffset)`, `RunAsync(CancellationToken)`. **`BackgroundConversationId` is removed** — Task 5 updates its only other caller.

- [ ] **Step 1: Write the failing tests**

Replace the `RunAsync_*` tests in `src/agent/OpenAgent.Tests/BackgroundAgentRunnerTests.cs` with these, and update the `Build` helper to supply a `DeliveryRouter` and return the provider:

```csharp
    private (BackgroundAgentRunner runner, InMemoryConversationStore store, AgentConfig config, SystemJobStateStore jobState)
        Build(string? mainId = MainId, ILlmTextProvider? provider = null)
    {
        var store = new InMemoryConversationStore();
        if (mainId is not null)
            store.GetOrCreate(mainId, "telegram", "p", "m", "vp", "vm");

        var config = new AgentConfig
        {
            BackgroundAgentEnabled = true,
            MainConversationId = mainId,
            TextProvider = "fake",
            TextModel = "m"
        };

        var jobStore = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        var environment = new AgentEnvironment { DataPath = _dataPath };
        Func<string, ILlmTextProvider> factory = _ => provider ?? new StreamingTextProvider("ok");

        var router = new DeliveryRouter(
            new NoopConnectionManager(),
            new NoopWebSocketRegistry(),
            NullLogger<DeliveryRouter>.Instance);

        var runner = new BackgroundAgentRunner(
            store, factory, environment, config, jobStore, router,
            NullLogger<BackgroundAgentRunner>.Instance);
        return (runner, store, config, jobStore);
    }

    // The heartbeat runs IN the main conversation - that is the whole point of the redesign.
    // It must not create a conversation of its own.
    [Fact]
    public async Task RunAsync_runs_the_turn_in_the_main_conversation()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "Monday's shot is not logged - did you take it?");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        var reply = Assert.Single(store.GetMessages(MainId), m => m.Role == "assistant");
        Assert.Contains("Monday's shot", reply.Content);
    }

    // The nudge is scaffolding, not conversation. It must never survive in Martin's thread.
    [Fact]
    public async Task RunAsync_does_not_persist_the_nudge()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "something worth saying");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        Assert.Empty(store.GetMessages(MainId).Where(m => m.Role == "user"));
        Assert.Single(provider.PersistedUserContents); // the provider DID receive a nudge
        Assert.Contains("[Heartbeat]", provider.PersistedUserContents[0]);
    }

    // A silent run must leave the thread exactly as it found it.
    [Fact]
    public async Task RunAsync_silent_turn_leaves_main_conversation_untouched()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "nothing new here.\n\n[]");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        Assert.Empty(store.GetMessages(MainId));
    }

    // If the completion throws, the nudge must still be cleaned up - otherwise a crash
    // leaves "[Heartbeat]" sitting in the user's chat.
    [Fact]
    public async Task RunAsync_removes_the_nudge_even_when_the_provider_throws()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var (runner, _, _, _) = BuildWith(store, new ThrowingTextProvider());

        await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(CancellationToken.None));

        Assert.Empty(store.GetMessages(MainId).Where(m => m.Role == "user"));
    }

    [Fact]
    public async Task RunAsync_no_ops_when_main_conversation_id_unset()
    {
        var (runner, store, config, _) = Build();
        config.MainConversationId = null;

        await runner.RunAsync(CancellationToken.None);

        Assert.Empty(store.GetMessages(MainId));
    }

    private (BackgroundAgentRunner runner, InMemoryConversationStore store, AgentConfig config, SystemJobStateStore jobState)
        BuildWith(InMemoryConversationStore store, ILlmTextProvider provider)
    {
        var config = new AgentConfig
        {
            BackgroundAgentEnabled = true,
            MainConversationId = MainId,
            TextProvider = "fake",
            TextModel = "m"
        };
        var jobStore = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        var environment = new AgentEnvironment { DataPath = _dataPath };
        var router = new DeliveryRouter(
            new NoopConnectionManager(),
            new NoopWebSocketRegistry(),
            NullLogger<DeliveryRouter>.Instance);

        var runner = new BackgroundAgentRunner(
            store, _ => provider, environment, config, jobStore, router,
            NullLogger<BackgroundAgentRunner>.Instance);
        return (runner, store, config, jobStore);
    }
```

Add these usings to the top of the file:

```csharp
using OpenAgent.ScheduledTasks;
```

Delete any existing test that references `BackgroundAgentRunner.BackgroundConversationId` or asserts a `"background"`-source conversation is created (e.g. `RunAsync_creates_background_conversation_with_correct_source`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~BackgroundAgentRunnerTests"`
Expected: FAIL to **compile** — `BackgroundAgentRunner` has no 7-argument constructor taking a `DeliveryRouter`. That is the expected red state.

- [ ] **Step 3: Rewrite the runner**

Replace the whole of `src/agent/OpenAgent.BackgroundAgent/BackgroundAgentRunner.cs` with:

```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks;
using OpenAgent.ScheduledTasks.SystemJobs;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// Orchestrates a single heartbeat. The heartbeat is a nudge, not a separate agent: it injects an
/// ephemeral user message into the user's main conversation and lets the agent take an ordinary
/// turn there - full history, all tools, its real system prompt.
///
/// Because the turn happens in the main conversation, the agent can see what it already said and
/// what the user answered. That is the whole design: perception and memory-of-speech are not
/// features, they are consequences of living in the thread. The previous architecture ran in an
/// isolated conversation with no way to read main, and re-asked the same questions six times in a
/// day - twice after the user had already answered them.
///
/// The nudge itself is never persisted. The agent either replies (the provider persists it, and we
/// deliver it to the bound channel) or emits the "[]" sentinel (the provider discards the whole
/// turn, and the thread is untouched).
/// </summary>
public sealed class BackgroundAgentRunner
{
    /// <summary>Name under which <see cref="BackgroundAgentJob"/> registers in system-jobs.json.</summary>
    public const string JobName = "background-agent";

    private static readonly TimeSpan MinSinceLastRun = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinSinceLastMainMessage = TimeSpan.FromMinutes(15);

    private readonly IConversationStore _store;
    private readonly Func<string, ILlmTextProvider> _textProviderResolver;
    private readonly AgentEnvironment _environment;
    private readonly AgentConfig _agentConfig;
    private readonly SystemJobStateStore _jobStateStore;
    private readonly DeliveryRouter _deliveryRouter;
    private readonly ILogger<BackgroundAgentRunner> _logger;

    public BackgroundAgentRunner(
        IConversationStore store,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentEnvironment environment,
        AgentConfig agentConfig,
        SystemJobStateStore jobStateStore,
        DeliveryRouter deliveryRouter,
        ILogger<BackgroundAgentRunner> logger)
    {
        _store = store;
        _textProviderResolver = textProviderResolver;
        _environment = environment;
        _agentConfig = agentConfig;
        _jobStateStore = jobStateStore;
        _deliveryRouter = deliveryRouter;
        _logger = logger;
    }

    /// <summary>
    /// Apply the gates from BACKGROUND.md. Returns true only when all are satisfied. The
    /// time-of-day window is owned by the cron ("*/15 6-21 * * *"); this method covers the master
    /// switch, configuration sanity, and the two interval gates.
    /// </summary>
    public Task<bool> ShouldRunAsync(DateTimeOffset now)
    {
        if (!_agentConfig.BackgroundAgentEnabled)
        {
            _logger.LogDebug("Background agent gated: AgentConfig.BackgroundAgentEnabled is false");
            return Task.FromResult(false);
        }

        if (string.IsNullOrWhiteSpace(_agentConfig.MainConversationId))
        {
            _logger.LogDebug("Background agent gated: AgentConfig.MainConversationId is not set");
            return Task.FromResult(false);
        }

        var mainConversation = _store.Get(_agentConfig.MainConversationId);
        if (mainConversation is null)
        {
            _logger.LogDebug("Background agent gated: main conversation '{ConversationId}' not found",
                _agentConfig.MainConversationId);
            return Task.FromResult(false);
        }

        // Gate 1: minimum interval since our previous successful run.
        var jobState = _jobStateStore.GetOrCreate(JobName);
        if (jobState.LastRunAt is { } lastRun && now - lastRun < MinSinceLastRun)
        {
            _logger.LogDebug("Background agent gated: only {Elapsed} since last run (need {Required})",
                now - lastRun, MinSinceLastRun);
            return Task.FromResult(false);
        }

        // Gate 2: minimum quiet period. Don't interrupt an active conversation.
        var messages = _store.GetMessages(_agentConfig.MainConversationId);
        var lastMessageAt = messages.Count == 0 ? (DateTimeOffset?)null : messages[^1].CreatedAt;
        if (lastMessageAt is { } lastMsg && now - lastMsg < MinSinceLastMainMessage)
        {
            _logger.LogDebug("Background agent gated: main conversation last active {Elapsed} ago (need {Required})",
                now - lastMsg, MinSinceLastMainMessage);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Run one heartbeat: inject the nudge into the main conversation, let the agent take a normal
    /// turn, remove the nudge, and deliver the reply if there is one.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var mainConversationId = _agentConfig.MainConversationId;
        if (string.IsNullOrWhiteSpace(mainConversationId))
        {
            _logger.LogWarning("Heartbeat skipped: AgentConfig.MainConversationId is not set");
            return;
        }

        var conversation = _store.Get(mainConversationId);
        if (conversation is null)
        {
            _logger.LogWarning("Heartbeat skipped: main conversation '{ConversationId}' not found", mainConversationId);
            return;
        }

        var nudge = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = mainConversationId,
            Role = "user",
            Content = BuildNudge(),
            Modality = MessageModality.Text
        };

        var provider = _textProviderResolver(conversation.TextProvider);
        var startedAt = DateTimeOffset.UtcNow;
        var reply = new StringBuilder();

        try
        {
            await foreach (var evt in provider.CompleteAsync(conversation, nudge, ct))
            {
                if (evt is TextDelta delta)
                    reply.Append(delta.Content);
            }
        }
        finally
        {
            // The nudge is scaffolding, never conversation. Remove it whether the agent spoke,
            // stayed silent, or threw - otherwise a crash mid-turn leaves "[Heartbeat]" sitting in
            // the user's chat. DeleteMessages is idempotent, so the suppressed path (where the
            // provider already deleted the whole turn) is safe to double-delete.
            _store.DeleteMessages(mainConversationId, [nudge.Id]);
        }

        var text = reply.ToString();

        if (ResponseSuppression.IsSuppressed(text))
        {
            _logger.LogInformation("Heartbeat silent in {Ms}ms — agent had nothing to say",
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Heartbeat produced no text; nothing to deliver");
            return;
        }

        try
        {
            // Re-fetch in case channel binding shifted during the completion.
            var current = _store.Get(mainConversationId) ?? conversation;
            await _deliveryRouter.DeliverAsync(current, text, ct);
            _logger.LogInformation("Heartbeat spoke: delivered {Length}-char message in {Ms}ms",
                text.Length, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // The reply is already in history; a delivery failure must not fail the job.
            _logger.LogError(ex, "Heartbeat delivery failed for conversation '{ConversationId}'", mainConversationId);
        }
    }

    /// <summary>
    /// Build the ephemeral nudge. BACKGROUND.md is loaded fresh each run and carried inline,
    /// because the main conversation's system prompt does not include it (SystemPromptBuilder only
    /// loaded it for the retired "background"-source conversation).
    /// </summary>
    private string BuildNudge()
    {
        var path = Path.Combine(_environment.DataPath, "BACKGROUND.md");
        var instructions = File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("[Heartbeat]");
        sb.AppendLine();
        if (instructions.Length > 0)
        {
            sb.AppendLine(instructions);
            sb.AppendLine();
        }
        sb.Append("Reflect on the conversation above. If there is nothing genuinely worth saying, "
                  + "reply with exactly [] and nothing else.");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~BackgroundAgentRunnerTests"`
Expected: PASS. If `BackgroundAgentEndpoints.cs` fails to compile (it references the removed `BackgroundConversationId`), that is expected — Task 5 fixes it. To keep this task green, apply the endpoint fix from Task 5 Step 2 now.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.BackgroundAgent/BackgroundAgentRunner.cs src/agent/OpenAgent.Tests/BackgroundAgentRunnerTests.cs
git commit -m "feat(heartbeat): run the background turn in the main conversation"
```

---

### Task 5: Delete post_to_main and the background conversation plumbing

With the heartbeat running in main, the agent replies directly. `post_to_main` — writing a letter to the conversation it now lives in — is dead code, as is the `"background"` system-prompt source.

**Files:**
- Delete: `src/agent/OpenAgent.BackgroundAgent/PostToMainTool.cs`
- Delete: `src/agent/OpenAgent.BackgroundAgent/BackgroundToolHandler.cs`
- Delete: `src/agent/OpenAgent.Tests/PostToMainToolTests.cs`
- Modify: `src/agent/OpenAgent.BackgroundAgent/ServiceCollectionExtensions.cs`
- Modify: `src/agent/OpenAgent.BackgroundAgent/BackgroundAgentEndpoints.cs:31`
- Modify: `src/agent/OpenAgent/SystemPromptBuilder.cs:34`

**Interfaces:**
- Consumes: `AgentConfig.MainConversationId`.
- Produces: `BackgroundAgentStateResponse.ConversationId` now carries the *main* conversation id (or `""` when unset).

- [ ] **Step 1: Delete the files**

```bash
git rm src/agent/OpenAgent.BackgroundAgent/PostToMainTool.cs
git rm src/agent/OpenAgent.BackgroundAgent/BackgroundToolHandler.cs
git rm src/agent/OpenAgent.Tests/PostToMainToolTests.cs
```

- [ ] **Step 2: Point the endpoint at the main conversation**

In `BackgroundAgentEndpoints.MapBackgroundAgentEndpoints`, add `AgentConfig agentConfig` to the `/state` handler's parameters and replace

```csharp
                ConversationId: BackgroundAgentRunner.BackgroundConversationId,
```

with

```csharp
                ConversationId: agentConfig.MainConversationId ?? "",
```

Add the using:

```csharp
using OpenAgent.Models.Configs;
```

- [ ] **Step 3: Drop the tool registrations**

Replace `src/agent/OpenAgent.BackgroundAgent/ServiceCollectionExtensions.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// DI registration for the background agent. Adds the runner and the ISystemJob wrapper.
/// Requires <c>AddSystemJobs</c> to have been registered separately - the system-job runner picks
/// the wrapper up automatically via <c>IEnumerable&lt;ISystemJob&gt;</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundAgent(this IServiceCollection services)
    {
        services.AddSingleton<BackgroundAgentRunner>();
        services.AddSingleton<ISystemJob, BackgroundAgentJob>();
        return services;
    }
}
```

- [ ] **Step 4: Remove the BackgroundOnly prompt mode**

In `src/agent/OpenAgent/SystemPromptBuilder.cs`, remove the `("BACKGROUND.md", PromptFileMode.BackgroundOnly),` entry from `FileMap`. Then remove the now-unused `BackgroundOnly` enum member, the `isBackground` local in `Build`, and any `source`-based branch that used it. Leave the `source` parameter on `Build` in place if other callers pass it; if the compiler reports it as unused, still leave the parameter (it is part of a public signature used across projects) but delete the dead `isBackground` computation.

BACKGROUND.md is now carried inline by the heartbeat nudge, so it must not also be injected into the system prompt.

- [ ] **Step 5: Build and test**

Run: `cd src/agent && dotnet build`
Expected: `0 Error(s)`. If anything still references `PostToMainTool`, `BackgroundToolHandler`, or `BackgroundConversationId`, remove those references.

Run: `cd src/agent && dotnet test`
Expected: all non-flaky tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A src/agent
git commit -m "refactor(heartbeat): delete post_to_main, BackgroundToolHandler and the background prompt source"
```

---

### Task 6: Rewrite the prompts

`BACKGROUND.md` is now the body of the nudge, so it must read as an instruction to the agent *in its own conversation* — not as a briefing for a separate background persona. Its inbox and sandbox sections describe machinery that no longer exists. It also takes over the daily-log duty: `AGENTS.md` says to write the log "at the end of every session", which never fires on a Telegram thread that has no end, and that is why no daily log has been written since 2026-07-06 and the digest has been starving.

**Files:**
- Rewrite: `src/agent/OpenAgent/defaults/BACKGROUND.md`
- Modify: `src/agent/OpenAgent/defaults/AGENTS.md:27-31`

**Interfaces:** none (content only).

- [ ] **Step 1: Rewrite BACKGROUND.md**

Replace the entire file with:

```markdown
# BACKGROUND.md — The Heartbeat

This is a heartbeat. No one is waiting for a reply. You have not been asked a
question — you have been given a moment to think.

You are in your own conversation, with your full history. You can see everything
that has been said, including everything *you* have said.

---

## What to Do

Read back over the conversation. Then consider:

- **What's next?** Something coming up that needs preparing for, or a deadline
  that just moved.
- **What's waiting?** Something you asked that never got answered, or something
  the user said they'd do.
- **What's unresolved?** A thread that was dropped mid-way, a question raised and
  never closed.
- **What connects?** Two things that looked unrelated and no longer do.

Use your tools. Query the database, read files, search memory, look things up.
Reflection with evidence beats reflection from the armchair.

---

## Housekeeping

Keep today's daily log current — `memory/YYYY-MM-DD.md`. Write down what has
happened, what was decided, what is still open. Not a transcript, just the signal.

This is the only input the nightly digest has. No daily log means `MEMORY.md`
goes stale and your long-term memory quietly stops working.

If something is worth remembering, write it down. Thoughts you don't record are
gone — this turn is discarded if you stay silent.

---

## When to Speak

**The bar is high. Most heartbeats should end in silence.**

Speak when:

- Something genuinely needs the user's attention now
- You found something that changes what they should do next
- A connection came together that is actually useful, not merely interesting

Do NOT speak to:

- Ask something you have already asked. **Look first — it is right there in the
  conversation.** If it went unanswered, they saw it and chose not to answer.
  Asking again is nagging, not diligence.
- Report that you ran, or that you found nothing
- Repeat what they already know
- Say something that could just as easily wait for the next real message

---

## Staying Silent

**To stay silent, reply with exactly `[]` and nothing else.**

No narration, no status line, no "nothing new to report", no explanation of why
you are staying quiet. The whole turn is discarded, so anything you write there
is thrown away — and if you write prose before the `[]`, it is not thrown away,
it lands in the user's chat.

If you have something to say, just say it. Speak normally, as yourself — this is
your conversation, not a notification channel. No prefixes, no announcements that
this is a background thought.

---

## Tone

Short. Direct. One or two sentences, then the relevant detail.

Not: "Hey! While doing my background reflection I came across something I thought
might interest you..."

But: "Monday's shot isn't logged. Did you take it?"
```

- [ ] **Step 2: Fix the daily-log trigger in AGENTS.md**

In `src/agent/OpenAgent/defaults/AGENTS.md`, replace the table row

```markdown
| `memory/YYYY-MM-DD.md` | Daily logs | Every session |
```

with

```markdown
| `memory/YYYY-MM-DD.md` | Daily logs | As things happen, and on every heartbeat |
```

and replace the line

```markdown
**Write things down.** If you want to remember something, put it in a file. "Mental notes" don't survive session restarts. Files do.
```

with

```markdown
**Write things down.** If you want to remember something, put it in a file. "Mental notes" don't survive session restarts. Files do.

Don't wait for the "end" of a session to write your daily log — a chat channel never ends. Update `memory/YYYY-MM-DD.md` as things happen, and top it up on every heartbeat.
```

- [ ] **Step 3: Build and test**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: `0 Error(s)`; all non-flaky tests pass. (`DataDirectoryBootstrapTests` may assert on the defaults — if it checks file *existence* only, it stays green.)

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent/defaults
git commit -m "docs(prompts): rewrite BACKGROUND.md as a heartbeat, give it the daily log"
```

---

### Task 7: Deploy and re-enable

The defaults ship in the assembly, but **`DataDirectoryBootstrap` never overwrites existing files** — the live `BACKGROUND.md` and `AGENTS.md` in the data directory will NOT be replaced by a redeploy. They must be updated in place.

Production: `https://openagent-eir.azurewebsites.net`, resource group `OpenAgent`, app `openagent-eir`. The API key is a hash-fragment token the user holds; ask for it rather than guessing.

**Files:** none (operations).

- [ ] **Step 1: Push and let CI build**

```bash
git push
gh run watch --exit-status
```

Expected: the deploy workflow succeeds. The Dockerfile runs `dotnet test`, so a broken image is never pushed.

- [ ] **Step 2: Restart the app service to pull the new image**

```bash
az webapp restart -g OpenAgent -n openagent-eir
```

Then confirm the new container is up by checking for a fresh `SystemJobRunner starting with {Count} job(s)` line in `GET /api/logs/log-<today>.jsonl?search=SystemJobRunner`.

- [ ] **Step 3: Update the live prompt files**

`BACKGROUND.md` and `AGENTS.md` in the data directory are stale copies. Overwrite them with the new content using the `file_write` tool via `POST /api/tools/file_write/execute` (body: `{"path": "BACKGROUND.md", "content": "..."}`).

For `AGENTS.md`, do **not** overwrite wholesale — it has been heavily customised for the health platform. Use `file_edit` to apply only the daily-log change from Task 6 Step 2.

- [ ] **Step 4: Remove the retired sandbox**

The `memory/background/` folder is dead. Its contents were audited against `MEMORY.md` on 2026-07-13 and every durable fact is already covered (see the spec's migration table). Delete it:

`POST /api/tools/shell_exec/execute` with `{"command": "rm -rf memory/background"}`.

Also delete the retired background conversation if it exists: `DELETE /api/conversations/system:background-agent`.

- [ ] **Step 5: Re-enable the heartbeat**

```bash
curl -s -X POST -H "X-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"backgroundAgentEnabled":"true"}' \
  https://openagent-eir.azurewebsites.net/api/admin/providers/agent/config
```

Verify: `GET /api/admin/providers/agent/values` reports `backgroundAgentEnabled = true`.

- [ ] **Step 6: Verify the first heartbeat**

Watch the logs for the next run (within 30 min, main quiet 15 min):

- `System job 'background-agent' starting` / `completed`
- Either `Heartbeat silent in {Ms}ms — agent had nothing to say`, or `Heartbeat spoke: delivered {Length}-char message`
- **`GET /api/conversations` — the main conversation must have NO user message containing `[Heartbeat]`.** This is the acceptance test for the whole redesign.
- Token totals on the main conversation must now increase even on silent runs (Task 2).
- No `system:background-agent` conversation is recreated.

Then confirm the memory chain restarts: a `memory/<today>.md` file should appear, and the 03:00 digest should report operations rather than `no operations needed`.

---

## Rollback

`backgroundAgentEnabled=false` via the admin endpoint disables the heartbeat instantly, with no restart and no redeploy. That is the first move if anything misbehaves.
