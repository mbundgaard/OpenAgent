# Context Management Rewrite — PR 3: Triggers, Iterative Summary, Cancellation, Logs

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Prerequisites:** PR 2 (feature/context-mgmt-pr2) merged or rebased onto master. `CompactionCutPoint`, `TokenEstimator`, `KeepRecentTokens`, per-model `ContextWindowTokens`, and the `<summary>`-wrapped user-role injection all in place. Tool-result blob storage from PR 1 is also in place.

**Goal:**
1. Extract a single `PerformCompactionAsync(conversationId, reason, customInstructions?, ct)` that serves threshold, overflow, and manual triggers. No more forked paths.
2. Split the compaction prompt into `Initial` + `Update` so repeated compactions merge into the prior summary rather than rewriting from scratch. Add a rule that preserves the *content* of memory-tool retrievals (`search_memory`, `load_memory_chunks`) in the summary.
3. Drop the unused `memories` field from `CompactionResult` / `ICompactionSummarizer` — it's extracted but never consumed.
4. Handle the latent bug where compaction loops silently when `AgentConfig.CompactionProvider` is unset. Skip cleanly + log once per startup.
5. Add proper cancellation: per-conversation `CancellationTokenSource` owned by the store, cancelled on `Delete` and host shutdown. Guard the cutoff swap against concurrent writes.
6. Expose `POST /api/conversations/{id}/compact` with optional `{"instructions": "..."}`.
7. Catch context-length errors in both providers, run overflow compaction, retry the same turn **once**, surface a clear error on a second overflow.
8. Emit structured `compaction.start` / `compaction.complete` / `compaction.error` log events. No UI affordance.

**Non-goals:**
- UI surfacing of compaction events. Remains out of scope; `Conversation.Context` stays as the only data surface.
- Memory pipeline changes. Compaction and memory remain separate lanes.
- Blob storage capping / offload. Deferred.
- `ConversationType` → `IsVoice` refactor. Still a separate issue.

**Spec:** [`2026-04-24-context-management-rewrite.md`](2026-04-24-context-management-rewrite.md)

---

## Chunk 1: Extract `PerformCompactionAsync` as the single compaction entry point

### Task 1: Introduce the `CompactionReason` enum

**Files:**
- Create: `src/agent/OpenAgent.Models/Conversations/CompactionReason.cs`

- [ ] **Step 1: Add the enum**

```csharp
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
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Success.

- [ ] **Step 3: Commit**

```
feat: add CompactionReason enum
```

---

### Task 2: Extract `PerformCompactionAsync` in `SqliteConversationStore`

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Add the new core method**

Below `TryStartCompaction`, add:

```csharp
/// <summary>
/// Core compaction path used by all three triggers. Acquires the CompactionRunning lock,
/// loads live messages (with full tool result blobs), walks the cut point, calls the
/// summarizer, and swaps CompactedUpToRowId + Context atomically. Returns true if a
/// cut was made, false if there was nothing to compact or if compaction is disabled.
/// Throws on summarizer errors (caller decides whether to retry).
/// </summary>
private async Task<bool> PerformCompactionAsync(
    string conversationId,
    CompactionReason reason,
    string? customInstructions,
    CancellationToken ct)
{
    if (_compactionSummarizer is null) return false;

    var conversation = Get(conversationId);
    if (conversation is null) return false;
    if (conversation.CompactionRunning) return false;

    // Acquire the running lock
    UpdateCompactionState(conversationId, compactionRunning: true, context: null, compactedUpToRowId: null);

    try
    {
        var liveMessages = ReadMessagesFromDb(conversationId, conversation.CompactedUpToRowId);
        LoadToolResultBlobs(conversationId, liveMessages);

        var cutIndex = CompactionCutPoint.Find(liveMessages, _compactionConfig.KeepRecentTokens);
        if (cutIndex is null or 0)
        {
            _logger.LogDebug("Nothing to compact for conversation {ConversationId} (reason: {Reason})",
                conversationId, reason);
            return false;
        }

        var toCompact = liveMessages.GetRange(0, cutIndex.Value);
        var newCutoffRowId = toCompact[^1].RowId;

        var result = await _compactionSummarizer.SummarizeAsync(
            conversation.Context, toCompact, customInstructions, ct);

        UpdateCompactionState(conversationId,
            compactionRunning: false,
            context: result.Context,
            compactedUpToRowId: newCutoffRowId);

        return true;
    }
    finally
    {
        // If an exception bailed us out before the success-path state swap, clear the lock.
        // The success path already cleared it via UpdateCompactionState above.
        var fresh = Get(conversationId);
        if (fresh?.CompactionRunning == true)
            UpdateCompactionState(conversationId, compactionRunning: false, context: null, compactedUpToRowId: null);
    }
}
```

Note: `SummarizeAsync` signature changes in Chunk 2 to accept `customInstructions` and a `CancellationToken`. For this task, call it with the current signature and fix up in Chunk 2. Or introduce the new signature now — your call; I'll keep them together as a single atomic delta in Chunk 2.

- [ ] **Step 2: Change `RunCompactionAsync` to delegate**

Replace its body with a call to `PerformCompactionAsync(conversation.Id, CompactionReason.Threshold, null, CancellationToken.None)`. Keep the method for now; remove in Task 4 once threshold calls go through the new core.

```csharp
private Task RunCompactionAsync(Conversation conversation) =>
    PerformCompactionAsync(conversation.Id, CompactionReason.Threshold, null, CancellationToken.None);
```

- [ ] **Step 3: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass. The single-path extraction is a pure refactor.

- [ ] **Step 4: Commit**

```
refactor(compaction): extract PerformCompactionAsync as the single entry point
```

---

### Task 3: Expose `CompactNowAsync` on `IConversationStore`

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/IConversationStore.cs`
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Modify: `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`

- [ ] **Step 1: Add the method to `IConversationStore`**

```csharp
/// <summary>
/// Triggers compaction on-demand. Returns true if a cut was made, false if there was
/// nothing to compact or if the summarizer is not configured. Used by the manual endpoint
/// and by provider overflow recovery.
/// </summary>
Task<bool> CompactNowAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default);
```

Add `using OpenAgent.Models.Conversations;` if needed.

- [ ] **Step 2: Implement in `SqliteConversationStore`**

```csharp
public Task<bool> CompactNowAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default)
    => PerformCompactionAsync(conversationId, reason, customInstructions, ct);
```

- [ ] **Step 3: Implement in `InMemoryConversationStore`**

The in-memory store doesn't run real summarization — return `false` (nothing happens):

```csharp
public Task<bool> CompactNowAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default)
    => Task.FromResult(false);
```

- [ ] **Step 4: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat: expose CompactNowAsync on IConversationStore
```

---

### Task 4: Route threshold trigger through the new entry point

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Rewrite `TryStartCompaction` to call `PerformCompactionAsync`**

```csharp
private void TryStartCompaction(Conversation conversation)
{
    if (_compactionSummarizer is null) return;
    if (conversation.CompactionRunning) return;
    if (conversation.LastPromptTokens is null) return;

    var window = conversation.ContextWindowTokens ?? _compactionConfig.MaxContextTokens;
    var triggerThreshold = window * _compactionConfig.CompactionTriggerPercent / 100;
    if (conversation.LastPromptTokens.Value < triggerThreshold) return;

    // Fire and forget — threshold compaction is background. Failures are logged.
    _ = Task.Run(async () =>
    {
        try
        {
            await PerformCompactionAsync(conversation.Id, CompactionReason.Threshold, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Threshold compaction failed for conversation {ConversationId}", conversation.Id);
        }
    });
}
```

- [ ] **Step 2: Remove `RunCompactionAsync`**

The stub introduced in Task 2 is no longer referenced.

- [ ] **Step 3: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 4: Commit**

```
refactor(compaction): route threshold trigger through PerformCompactionAsync
```

---

## Chunk 2: Iterative summary — Initial/Update prompts, drop memories field

### Task 5: Split `CompactionPrompt` into `Initial` and `Update`

**Files:**
- Modify: `src/agent/OpenAgent.Compaction/CompactionPrompt.cs`

- [ ] **Step 1: Replace the single `System` constant with two prompts**

```csharp
namespace OpenAgent.Compaction;

internal static class CompactionPrompt
{
    public const string Initial = """
        You are a conversation compactor. Your job is to produce a structured summary of conversation messages.

        ## Input

        You receive a list of conversation messages to compact.

        ## Output

        Respond with a JSON object containing:
        - "context": the structured summary (string)

        ## Structure

        Group messages by topic, include timestamps and message references:

        ```
        ## Topic Name (YYYY-MM-DD HH:mm - HH:mm)
        Key decisions, outcomes, and facts from this topic.
        What was discussed, what was decided, what was the result.
        [ref: msg_id1, msg_id2, msg_id3]
        ```

        ## Rules

        - Group related messages by topic, not chronologically
        - Include timestamps (from message CreatedAt) for each topic section
        - Reference message IDs using [ref: id1, id2, ...] — only user and assistant messages, NOT tool result messages
        - For tool calls: summarize what was attempted and the outcome, reference the tool call message ID
        - For tool results: capture the outcome in your summary text, do NOT reference the tool result message ID
        - Prioritize: decisions made, facts established, outcomes of actions
        - Be concise but preserve enough detail that the agent can decide whether to expand references
        - **When the conversation includes `search_memory` or `load_memory_chunks` tool calls, preserve the CONTENT of what was retrieved — not just the fact that a lookup happened. The summary is the agent's working memory after the cut; if the content is missing, the agent will waste tokens re-searching for things it already knows it knows.**
        """;

    public const string Update = """
        You are a conversation compactor. You are UPDATING an existing structured summary by merging new conversation messages into it.

        ## Input

        1. A `<previous-summary>` block containing the summary from the last compaction cycle.
        2. A list of NEW conversation messages that have occurred since.

        ## Output

        Respond with a JSON object containing:
        - "context": the updated structured summary (string)

        ## Merge Rules

        - PRESERVE all existing topics, decisions, and [ref: ...] references from the previous summary — they remain valid.
        - APPEND new topics from the new messages, or EXTEND existing topics with new facts and refs.
        - If a previous topic's status changed (e.g. "In Progress" → "Done"), update it in place.
        - Keep the same topic-grouped format and timestamp conventions.
        - **When the new messages include `search_memory` or `load_memory_chunks` tool calls, preserve the CONTENT of what was retrieved — not just the fact that a lookup happened. The summary is the agent's working memory after the cut.**

        Output the full updated summary; the previous summary will be discarded after your response.
        """;
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Success (summarizer still references `CompactionPrompt.System` — Task 6 fixes).

Actually that's wrong — deleting `CompactionPrompt.System` will break the summarizer's compile. Merge Task 5 and Task 6 into one atomic change to keep the tree green. (Re-order: do Task 6 first, then do this edit.)

**Preferred ordering:** perform Task 6 (summarizer changes) and this Task 5 (prompt split) in a single commit. Skip the separate commit for Task 5 alone.

---

### Task 6: Update `CompactionSummarizer` to use the split prompts, drop `memories`

**Files:**
- Modify: `src/agent/OpenAgent.Compaction/CompactionSummarizer.cs`
- Modify: `src/agent/OpenAgent.Contracts/ICompactionSummarizer.cs`

- [ ] **Step 1: Drop `Memories` from `CompactionResult`**

In `ICompactionSummarizer.cs`:

```csharp
public sealed class CompactionResult
{
    /// <summary>Structured summary with topic grouping, timestamps, and [ref: ...] references.</summary>
    public required string Context { get; init; }
}
```

(Remove the `Memories` property entirely.)

- [ ] **Step 2: Add `customInstructions` and `ct` params to `ICompactionSummarizer.SummarizeAsync`**

```csharp
Task<CompactionResult> SummarizeAsync(
    string? existingContext,
    IReadOnlyList<Message> messages,
    string? customInstructions = null,
    CancellationToken ct = default);
```

- [ ] **Step 3: Rewrite `CompactionSummarizer.SummarizeAsync`**

```csharp
public async Task<CompactionResult> SummarizeAsync(
    string? existingContext,
    IReadOnlyList<Message> messages,
    string? customInstructions = null,
    CancellationToken ct = default)
{
    var systemPrompt = existingContext is null
        ? CompactionPrompt.Initial
        : CompactionPrompt.Update;

    var userContent = new StringBuilder();

    if (existingContext is not null)
    {
        userContent.AppendLine("<previous-summary>");
        userContent.AppendLine(existingContext);
        userContent.AppendLine("</previous-summary>");
        userContent.AppendLine();
    }

    if (!string.IsNullOrWhiteSpace(customInstructions))
    {
        userContent.AppendLine("<focus>");
        userContent.AppendLine(customInstructions.Trim());
        userContent.AppendLine("</focus>");
        userContent.AppendLine();
    }

    userContent.AppendLine("## Messages to Compact");
    foreach (var msg in messages)
    {
        userContent.AppendLine($"[{msg.Id}] [{msg.CreatedAt:yyyy-MM-dd HH:mm}] [{msg.Role}]: {msg.Content}");
        if (msg.ToolCalls is not null)
            userContent.AppendLine($"  Tool calls: {msg.ToolCalls}");
        if (msg.ToolCallId is not null)
            userContent.AppendLine($"  (tool result for call {msg.ToolCallId})");
        if (!string.IsNullOrEmpty(msg.FullToolResult))
            userContent.AppendLine($"  Full content: {TruncateForSummary(msg.FullToolResult, TOOL_RESULT_MAX_CHARS)}");
    }

    var llmMessages = new List<Message>
    {
        new() { Id = "sys", ConversationId = "", Role = "system", Content = systemPrompt },
        new() { Id = "usr", ConversationId = "", Role = "user", Content = userContent.ToString() }
    };

    var provider = _providerFactory(_agentConfig.CompactionProvider);
    var options = new CompletionOptions { ResponseFormat = "json_object" };

    var fullContent = new StringBuilder();
    await foreach (var evt in provider.CompleteAsync(llmMessages, _agentConfig.CompactionModel, options, ct))
    {
        if (evt is TextDelta delta)
            fullContent.Append(delta.Content);
    }

    using var doc = JsonDocument.Parse(fullContent.ToString());
    var context = doc.RootElement.GetProperty("context").GetString()!;

    _logger.LogInformation("Compaction summary generated: {Length} chars (mode: {Mode})",
        context.Length, existingContext is null ? "initial" : "update");

    return new CompactionResult { Context = context };
}

private const int TOOL_RESULT_MAX_CHARS = 2000;

private static string TruncateForSummary(string text, int maxChars)
{
    if (text.Length <= maxChars) return text;
    var truncated = text.Length - maxChars;
    return $"{text.Substring(0, maxChars)}\n\n[... {truncated} more characters truncated]";
}
```

Add `using System.Text;` if not present.

- [ ] **Step 4: Update `SqliteConversationStore.PerformCompactionAsync` to pass `customInstructions` and `ct`**

```csharp
var result = await _compactionSummarizer.SummarizeAsync(
    conversation.Context, toCompact, customInstructions, ct);
```

- [ ] **Step 5: Update the test `FakeCompactionSummarizer` to accept the new signature**

In `SqliteConversationStoreTests.cs`:

```csharp
private sealed class FakeCompactionSummarizer(string context) : ICompactionSummarizer
{
    public IReadOnlyList<Message>? LastMessages { get; private set; }
    public string? LastExistingContext { get; private set; }
    public string? LastCustomInstructions { get; private set; }

    public Task<CompactionResult> SummarizeAsync(
        string? existingContext,
        IReadOnlyList<Message> messages,
        string? customInstructions = null,
        CancellationToken ct = default)
    {
        LastExistingContext = existingContext;
        LastMessages = messages;
        LastCustomInstructions = customInstructions;
        return Task.FromResult(new CompactionResult { Context = context });
    }
}
```

- [ ] **Step 6: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 7: Commit**

```
feat(compaction): iterative prompts with memory-retrieval preservation, drop memories
```

(Rolls Task 5 and Task 6 into one commit to keep the tree green.)

---

## Chunk 3: Error handling and cancellation

### Task 7: Skip compaction cleanly when `CompactionProvider` is unset

**Files:**
- Modify: `src/agent/OpenAgent.Compaction/CompactionSummarizer.cs`
- Test: `src/agent/OpenAgent.Tests/CompactionSummarizerTests.cs` (new)

- [ ] **Step 1: Add an explicit check + log-once**

```csharp
private bool _providerUnsetLogged;

public async Task<CompactionResult> SummarizeAsync(...)
{
    if (string.IsNullOrWhiteSpace(_agentConfig.CompactionProvider)
        || string.IsNullOrWhiteSpace(_agentConfig.CompactionModel))
    {
        if (!_providerUnsetLogged)
        {
            _providerUnsetLogged = true;
            _logger.LogWarning(
                "Compaction skipped: AgentConfig.CompactionProvider or CompactionModel is unset. " +
                "Configure them to enable automatic and manual compaction.");
        }
        throw new CompactionDisabledException();
    }

    // ... existing body ...
}
```

Add the exception type at the bottom of the file:

```csharp
public sealed class CompactionDisabledException : Exception
{
    public CompactionDisabledException()
        : base("Compaction is disabled because CompactionProvider or CompactionModel is unset.") { }
}
```

- [ ] **Step 2: Handle the exception in `PerformCompactionAsync`**

Wrap the `SummarizeAsync` call:

```csharp
try
{
    var result = await _compactionSummarizer.SummarizeAsync(...);
    UpdateCompactionState(...);
    return true;
}
catch (CompactionDisabledException)
{
    _logger.LogDebug("Compaction skipped for {ConversationId} — disabled", conversationId);
    return false;
}
```

- [ ] **Step 3: Add a test**

Create `src/agent/OpenAgent.Tests/CompactionSummarizerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Compaction;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class CompactionSummarizerTests
{
    [Fact]
    public async Task Unset_provider_throws_CompactionDisabledException_and_logs_once()
    {
        var config = new AgentConfig(); // CompactionProvider = "", CompactionModel = ""
        Func<string, ILlmTextProvider> factory = _ => throw new InvalidOperationException("should not be called");
        var summarizer = new CompactionSummarizer(factory, config, NullLogger<CompactionSummarizer>.Instance);

        await Assert.ThrowsAsync<CompactionDisabledException>(() =>
            summarizer.SummarizeAsync(null, []));

        // Second call also throws — but only the first logs.
        await Assert.ThrowsAsync<CompactionDisabledException>(() =>
            summarizer.SummarizeAsync(null, []));
    }
}
```

- [ ] **Step 4: Build and run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat(compaction): skip cleanly when CompactionProvider is unset
```

---

### Task 8: Per-conversation cancellation token registry

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Add the registry**

Near the top of the class:

```csharp
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _compactionCts = new();
```

- [ ] **Step 2: Register/cancel at PerformCompactionAsync boundaries**

```csharp
private async Task<bool> PerformCompactionAsync(
    string conversationId, CompactionReason reason, string? customInstructions, CancellationToken ct)
{
    // ... existing guards ...

    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    if (!_compactionCts.TryAdd(conversationId, cts))
    {
        cts.Dispose();
        return false; // already running
    }

    try
    {
        // Existing body — use cts.Token for the summarizer call
        var result = await _compactionSummarizer.SummarizeAsync(
            conversation.Context, toCompact, customInstructions, cts.Token);
        // ...
    }
    finally
    {
        _compactionCts.TryRemove(conversationId, out _);
        cts.Dispose();
    }
}
```

- [ ] **Step 3: Cancel on `Delete`**

In the existing `Delete` method, before the SQL deletes:

```csharp
if (_compactionCts.TryRemove(conversationId, out var cts))
{
    cts.Cancel();
    cts.Dispose();
}
```

- [ ] **Step 4: Dispose all on store shutdown**

Update `Dispose`:

```csharp
public void Dispose()
{
    foreach (var cts in _compactionCts.Values)
    {
        try { cts.Cancel(); cts.Dispose(); } catch { /* ignore */ }
    }
    _compactionCts.Clear();
}
```

- [ ] **Step 5: Add a test**

```csharp
[Fact]
public async Task Delete_cancels_in_flight_compaction()
{
    var gate = new TaskCompletionSource();
    var summarizer = new GatedSummarizer(gate.Task);
    var env = new AgentEnvironment { DataPath = _dbDir };
    using var store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance,
        new CompactionConfig { KeepRecentTokens = 1 }, summarizer);

    store.GetOrCreate("conv1", "test", ConversationType.Text, "p", "m");
    for (var i = 0; i < 4; i++)
        store.AddMessage("conv1", new Message { Id = $"m{i}", ConversationId = "conv1", Role = i % 2 == 0 ? "user" : "assistant", Content = $"message {i}" });

    var compactTask = store.CompactNowAsync("conv1", CompactionReason.Manual);

    // Give the background task a moment to enter SummarizeAsync
    await Task.Delay(50);

    store.Delete("conv1");
    gate.SetResult(); // release the summarizer

    // Compaction should either return false (cancelled before success) or throw OperationCanceledException
    await Assert.ThrowsAnyAsync<Exception>(() => compactTask);
}

private sealed class GatedSummarizer(Task gate) : ICompactionSummarizer
{
    public async Task<CompactionResult> SummarizeAsync(
        string? existingContext, IReadOnlyList<Message> messages,
        string? customInstructions = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        return new CompactionResult { Context = "done" };
    }
}
```

- [ ] **Step 6: Build and run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 7: Commit**

```
feat(compaction): per-conversation cancellation registry with Delete cascade
```

---

### Task 9: Cutoff race guard — snapshot `LastRowId` before summarization

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

Context: under the current code, a message persisted during the LLM summarization call can have its `rowid` erroneously "eaten" by the new `CompactedUpToRowId`. Snapshot the max rowid at start and cap the cutoff.

- [ ] **Step 1: Snapshot and cap**

In `PerformCompactionAsync`:

```csharp
var liveMessages = ReadMessagesFromDb(conversationId, conversation.CompactedUpToRowId);
LoadToolResultBlobs(conversationId, liveMessages);

// Snapshot the max rowid seen at this moment. Any message inserted by a concurrent
// turn after this point must NOT be absorbed into the compaction cutoff.
var maxRowIdAtSnapshot = liveMessages.Count > 0 ? liveMessages[^1].RowId : conversation.CompactedUpToRowId ?? 0;

var cutIndex = CompactionCutPoint.Find(liveMessages, _compactionConfig.KeepRecentTokens);
if (cutIndex is null or 0) return false;

var toCompact = liveMessages.GetRange(0, cutIndex.Value);
var newCutoffRowId = toCompact[^1].RowId;

// Defense in depth — the snapshot contains toCompact's rowids, so this is always true,
// but make the invariant explicit.
if (newCutoffRowId > maxRowIdAtSnapshot)
    throw new InvalidOperationException("Cutoff exceeds snapshot — logic error in cut-point algorithm.");

var result = await _compactionSummarizer.SummarizeAsync(...);
UpdateCompactionState(...);
```

The invariant holds trivially with the current cut algorithm (toCompact is a prefix of the snapshot), but the assertion documents it and protects against regressions.

- [ ] **Step 2: Build and run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 3: Commit**

```
feat(compaction): snapshot LastRowId and assert cutoff ≤ snapshot invariant
```

---

## Chunk 4: Manual trigger endpoint

### Task 10: Expose `POST /api/conversations/{id}/compact`

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/ConversationEndpoints.cs` (or wherever conversation endpoints live)
- Test: `src/agent/OpenAgent.Tests/ConversationEndpointTests.cs` (new or extend existing)

- [ ] **Step 1: Find the existing conversation endpoints file**

Look in `src/agent/OpenAgent.Api/Endpoints/ConversationEndpoints.cs`. If it doesn't exist, check `ChatEndpoints.cs` or nearby — conversation management endpoints may be grouped.

- [ ] **Step 2: Add the manual compact endpoint**

```csharp
conversations.MapPost("/{conversationId}/compact", async (string conversationId, CompactRequest? body, IConversationStore store, CancellationToken ct) =>
{
    if (store.Get(conversationId) is null)
        return Results.NotFound();

    var compacted = await store.CompactNowAsync(
        conversationId,
        CompactionReason.Manual,
        body?.Instructions,
        ct);

    return Results.Ok(new { compacted });
})
.RequireAuthorization();
```

And the request DTO:

```csharp
public sealed record CompactRequest([property: System.Text.Json.Serialization.JsonPropertyName("instructions")] string? Instructions);
```

- [ ] **Step 3: Write an integration test**

```csharp
[Fact]
public async Task Compact_returns_ok_with_compacted_true_when_cut_happens()
{
    // Test: seed a conversation with enough messages to trigger compaction,
    // POST to /api/conversations/{id}/compact, assert response is { compacted: true }
    // Uses the FakeCompactionSummarizer registered via WebApplicationFactory.
    // (See ChatEndpointTests for the WebApplicationFactory pattern.)
}

[Fact]
public async Task Compact_returns_not_found_for_unknown_conversation()
{
    var client = CreateAuthenticatedClient();
    var response = await client.PostAsJsonAsync("/api/conversations/does-not-exist/compact", new { });
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}
```

(Concrete WebApplicationFactory setup copies the pattern from `ChatEndpointTests`.)

- [ ] **Step 4: Build and run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat(api): add POST /api/conversations/{id}/compact with optional instructions
```

---

## Chunk 5: Overflow trigger

### Task 11: Add `IsContextOverflowError` helper in each provider

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`

The shapes are different across providers. Keep the detection private to each.

- [ ] **Step 1: Azure detection**

Azure returns `400 Bad Request` with a JSON body containing `error.code == "context_length_exceeded"` or `message` mentioning "maximum context length":

```csharp
private static bool IsContextOverflow(HttpStatusCode status, string body)
{
    if (status != HttpStatusCode.BadRequest) return false;
    return body.Contains("context_length_exceeded", StringComparison.Ordinal)
        || body.Contains("maximum context length", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Anthropic detection**

Anthropic returns `400 Bad Request` with `type: "invalid_request_error"` and a message mentioning "prompt is too long" or "tokens":

```csharp
private static bool IsContextOverflow(HttpStatusCode status, string body)
{
    if (status != HttpStatusCode.BadRequest) return false;
    return body.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
        || body.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
        && body.Contains("context", StringComparison.OrdinalIgnoreCase);
}
```

(Tune during the implementation — these heuristics aren't perfect. Consider adding a unit test with captured error bodies.)

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Success.

- [ ] **Step 4: Commit**

```
feat(providers): add IsContextOverflow detection helpers
```

---

### Task 12: Overflow retry in Azure OpenAI provider

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`

- [ ] **Step 1: Wrap the completion loop with one overflow-retry**

In `CompleteAsync`, around the existing loop, add overflow detection. On first overflow:
1. Call `agentLogic.CompactAsync(conversation.Id, CompactionReason.Overflow, null, ct)` — synchronous from the caller's viewpoint.
2. Reload stored messages (the cut happened).
3. Rebuild `request.Messages` via `BuildChatMessages(conversation)`.
4. Retry.

On second overflow (same turn):
- Throw an `HttpRequestException` with a clear message. Surface to caller.

Implementation sketch (refactor as needed — the existing loop is inside the tool-call retry loop, so you may need nested try/catch or a helper):

```csharp
var overflowRetried = false;
while (true)
{
    var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = JsonContent.Create(request)
    };
    var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

    if (!httpResponse.IsSuccessStatusCode)
    {
        var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
        if (!overflowRetried && IsContextOverflow(httpResponse.StatusCode, errorBody))
        {
            logger.LogWarning("Azure OpenAI context overflow for conversation {ConversationId} — compacting and retrying once", conversationId);
            overflowRetried = true;
            // `agentLogic` doesn't currently expose Compact — call the store via DI directly, or add a method. See Task 14.
            var compacted = await _conversationStore.CompactNowAsync(conversationId, CompactionReason.Overflow, null, ct);
            if (!compacted)
            {
                throw new HttpRequestException("Context overflow, and compaction could not reduce history (already minimal).");
            }
            // Rebuild messages from the compacted state
            request.Messages = BuildChatMessages(agentLogic.GetConversation(conversationId)!);
            continue;
        }

        logger.LogError("Azure OpenAI returned {StatusCode} for conversation {ConversationId}: {ErrorBody}", ...);
        throw new HttpRequestException(...);
    }

    break; // success — fall through to stream parsing
}
```

Note: the provider currently doesn't have an `IConversationStore` dependency — it goes through `IAgentLogic`. Options:
- Add `CompactAsync` to `IAgentLogic` (Task 14).
- Inject `IConversationStore` directly into the provider.

Preferred: Task 14 adds the `IAgentLogic.CompactAsync` pass-through.

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Success (after Task 14 lands).

- [ ] **Step 3: Commit**

```
feat(azure-openai): recover from context overflow with compact + retry-once
```

---

### Task 13: Overflow retry in Anthropic provider

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`

- [ ] **Step 1: Mirror Azure's overflow-retry pattern**

Same structure as Task 12, but with Anthropic's error body detection. Rebuild `messages` via `BuildMessages(conversation)` after compaction.

- [ ] **Step 2: Build and run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 3: Commit**

```
feat(anthropic): recover from context overflow with compact + retry-once
```

---

### Task 14: Add `CompactAsync` pass-through on `IAgentLogic`

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/IAgentLogic.cs`
- Modify: `src/agent/OpenAgent/AgentLogic.cs`

- [ ] **Step 1: Add the method to `IAgentLogic`**

```csharp
/// <summary>
/// Triggers compaction on-demand. Used by providers for overflow recovery. Thin
/// pass-through to IConversationStore.CompactNowAsync.
/// </summary>
Task<bool> CompactAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `AgentLogic`**

```csharp
public Task<bool> CompactAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default)
    => store.CompactNowAsync(conversationId, reason, customInstructions, ct);
```

- [ ] **Step 3: Update Azure and Anthropic overflow paths to call `agentLogic.CompactAsync(...)`**

Swap the direct `_conversationStore.CompactNowAsync` call for `agentLogic.CompactAsync` — preserves the "IAgentLogic is injected context, not orchestrator" principle (provider goes through the logic layer, not the store).

- [ ] **Step 4: Build and run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat: add IAgentLogic.CompactAsync as provider-facing compaction trigger
```

---

### Task 15: Test overflow retry

**Files:**
- Modify: `src/agent/OpenAgent.Tests/` (new file or add to an existing provider-level test)

- [ ] **Step 1: Test using a mock `HttpMessageHandler`**

Exercise the retry path by having the handler return a `context_length_exceeded` 400 on the first call and a real 200 SSE on the second. Verify: one compaction call happened, the second request was sent, and the provider's final result reflects the second response.

If mocking `HttpClient` directly is painful, an alternative: extract the HTTP-sending logic behind an interface and mock that. For PR 3, the simplest practical test is at the `IAgentLogic.CompactAsync` level — verify that when `CompactAsync` is called with `Overflow`, compaction actually runs. The full end-to-end HTTP retry is best verified via an integration test against a real small-context model once available.

At minimum:

```csharp
[Fact]
public async Task Overflow_reason_triggers_CompactNowAsync_via_AgentLogic()
{
    // Verify IAgentLogic.CompactAsync with CompactionReason.Overflow flows to the store.
}
```

- [ ] **Step 2: Commit**

```
test(compaction): overflow trigger smoke test via IAgentLogic.CompactAsync
```

---

## Chunk 6: Structured logs

### Task 16: Emit `compaction.start` / `compaction.complete` / `compaction.error`

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Add structured log entries in `PerformCompactionAsync`**

Use Serilog's named-property logging so the events are queryable via `/api/logs?search=compaction.complete`:

```csharp
var sw = System.Diagnostics.Stopwatch.StartNew();
_logger.LogInformation(
    "{@Event}",
    new { @event = "compaction.start", conversationId, reason = reason.ToString() });

try
{
    // ... cut point + summarize ...

    sw.Stop();
    _logger.LogInformation(
        "{@Event}",
        new
        {
            @event = "compaction.complete",
            conversationId,
            reason = reason.ToString(),
            messagesCompacted = toCompact.Count,
            tokensBefore = conversation.LastPromptTokens,
            durationMs = sw.ElapsedMilliseconds
        });

    return true;
}
catch (CompactionDisabledException)
{
    // Logged once per startup in the summarizer — don't double-log here.
    return false;
}
catch (OperationCanceledException)
{
    _logger.LogInformation(
        "{@Event}",
        new { @event = "compaction.cancelled", conversationId, reason = reason.ToString() });
    return false;
}
catch (Exception ex)
{
    _logger.LogError(ex,
        "{@Event}",
        new { @event = "compaction.error", conversationId, reason = reason.ToString(), error = ex.Message });
    throw;
}
```

- [ ] **Step 2: Build and run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 3: Manual verification**

Run the app. Trigger compaction (any path). Query `/api/logs?search=compaction.complete` and confirm the structured fields are present.

- [ ] **Step 4: Commit**

```
feat(compaction): structured log events (start/complete/error/cancelled)
```

---

## Verification

- [ ] **Step 1: Final full test run**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 2: Manual smoke — threshold still works**

Send enough messages to cross the threshold (or tune `KeepRecentTokens` low for the test). Confirm compaction fires in the background and logs `compaction.start` + `compaction.complete`.

- [ ] **Step 3: Manual smoke — manual endpoint**

```bash
curl -X POST \
  -H "X-Api-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{"instructions": "focus on auth decisions"}' \
  http://localhost:8080/api/conversations/<id>/compact
```

Expect `{ "compacted": true }` plus a `compaction.complete` log with `reason=Manual`.

- [ ] **Step 4: Manual smoke — overflow retry**

Hard to simulate without hitting a real context limit. A synthetic test: set `AgentConfig.TextModel` to a deployment with a small max-context, fill the conversation, submit a long turn. Confirm the provider logs overflow recovery and the final response reflects the compacted context.

- [ ] **Step 5: Manual smoke — unset provider doesn't loop**

Blank out `AgentConfig.CompactionProvider`. Trigger compaction. Confirm a single `WARN` log "Compaction skipped: AgentConfig.CompactionProvider ... is unset" appears and no further compaction attempts are made on subsequent triggers.

- [ ] **Step 6: Manual smoke — delete during compaction**

Hard to script. Optional: add a delay to the fake summarizer locally, delete the conversation mid-run, confirm the task is cancelled and no stale row is written.

---

## Out of Scope

- UI surface for compaction events. `Conversation.Context` and `CompactedUpToRowId` remain the API data surface.
- Blob size caps / archival. Still deferred until growth is measured.
- `ConversationType → IsVoice` refactor. Separate issue.
- Per-`Source` compaction prompt variants (Telegram/WhatsApp). Revisit if real-world chat summaries look wrong.
- Compaction backoff / scheduling. If threshold trigger races with itself, `CompactionRunning` blocks the second call — that's enough for now.
