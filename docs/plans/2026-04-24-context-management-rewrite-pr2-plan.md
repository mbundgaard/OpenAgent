# Context Management Rewrite — PR 2: Smarter Cut Point, User-Role Summary, Per-Model Window

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Prerequisites:** PR 1 (feature/context-mgmt-pr1) merged or rebased onto master. Tool results are persisted to disk with `ToolResultRef`, and `GetMessages(includeToolResultBlobs: true)` hydrates `FullToolResult` from disk.

**Goal:**
1. Stop cutting compaction mid-turn. Replace the count-based cut with a token-walk that always snaps to a user/assistant boundary — never between an assistant's `tool_calls` and its `tool` results.
2. Move the compaction summary out of `GetMessages` (where it's injected as a synthetic `system` message) and into the provider's request-build path, wrapped in a `<summary>` user message. The UI sees only post-cut messages; the LLM sees the summary. Keeps the real system prompt stable across turns (Anthropic caching).
3. Drive the compaction threshold from the active model's context window, not a hardcoded 400k constant.

**Non-goals:**
- Overflow-triggered compaction. Deferred to PR 3.
- Manual `/compact` endpoint. Deferred to PR 3.
- Iterative summary prompt split (`Initial` / `Update`). Deferred to PR 3.
- Cancellation and concurrency hardening of the compaction task. Deferred to PR 3.

**Spec:** [`2026-04-24-context-management-rewrite.md`](2026-04-24-context-management-rewrite.md)

---

## Chunk 1: Summary injection moves from store to providers

The current `GetMessages` prepends `Conversation.Context` as a synthetic `system` message. That breaks system-prompt caching (Anthropic especially) because the "system" changes each compaction. Move the summary into the provider's request builder and wrap it in `<summary>` tags as a `user` message. UI-facing `GetMessages` stops emitting the synthetic message entirely.

### Task 1: Remove summary prepend from `SqliteConversationStore.GetMessages`

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Update the existing compaction test to reflect new behavior**

In `SqliteConversationStoreTests.cs::GetMessages_excludes_compacted_messages`, change the assertions. Before (current):

```csharp
// Now GetMessages should only return msg3 plus the context
var messages = _store.GetMessages("conv1");

Assert.Equal(2, messages.Count);
Assert.Equal("system", messages[0].Role);
Assert.Contains("Old conversation about greetings", messages[0].Content);
Assert.Equal("msg3", messages[1].Id);
```

After:

```csharp
// GetMessages now returns ONLY post-cut messages. The summary lives on
// Conversation.Context for providers/UI to render separately.
var messages = _store.GetMessages("conv1");

Assert.Single(messages);
Assert.Equal("msg3", messages[0].Id);

var conv = _store.Get("conv1");
Assert.NotNull(conv);
Assert.Contains("Old conversation about greetings", conv.Context);
```

- [ ] **Step 2: Do the same for `Compaction_summarizes_old_messages_and_updates_cutoff`**

Before:

```csharp
var messages = store.GetMessages("conv1");

Assert.Equal("system", messages[0].Role);
Assert.Contains("Test summary", messages[0].Content);
Assert.Equal("msg5", messages[1].Id);
Assert.Equal("msg6", messages[2].Id);
Assert.Equal(3, messages.Count);
```

After:

```csharp
var messages = store.GetMessages("conv1");

Assert.Equal(2, messages.Count);
Assert.Equal("msg5", messages[0].Id);
Assert.Equal("msg6", messages[1].Id);

var freshConv = store.Get("conv1");
Assert.NotNull(freshConv);
Assert.Contains("Test summary", freshConv.Context);
```

- [ ] **Step 3: Run tests — expect failures**

Run: `cd src/agent && dotnet test --filter "SqliteConversationStoreTests"`
Expected: `GetMessages_excludes_compacted_messages` and `Compaction_summarizes_old_messages_and_updates_cutoff` FAIL (store still prepends the context message).

- [ ] **Step 4: Remove the prepend in `GetMessages`**

Change the method body from:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
{
    var conversation = Get(conversationId);
    var list = new List<Message>();

    // Prepend compaction summary as a system message if present
    if (conversation?.Context is not null)
    {
        list.Add(new Message
        {
            Id = "context",
            ConversationId = conversationId,
            Role = "system",
            Content = conversation.Context
        });
    }

    list.AddRange(ReadMessagesFromDb(conversationId, conversation?.CompactedUpToRowId));

    if (includeToolResultBlobs)
        LoadToolResultBlobs(conversationId, list);

    return list;
}
```

To:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
{
    var conversation = Get(conversationId);
    var list = ReadMessagesFromDb(conversationId, conversation?.CompactedUpToRowId);

    if (includeToolResultBlobs)
        LoadToolResultBlobs(conversationId, list);

    return list;
}
```

The compaction summary is no longer synthesized into the message list. Callers that need it read `Conversation.Context` directly.

- [ ] **Step 5: Run tests — expect pass**

Run: `cd src/agent && dotnet test --filter "SqliteConversationStoreTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```
refactor(store): GetMessages returns post-cut messages only, no synthetic context
```

---

### Task 2: Same change in `InMemoryConversationStore`

**Files:**
- Modify: `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`

- [ ] **Step 1: Remove the synthetic context message from `GetMessages`**

Change:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
{
    var conversation = Get(conversationId);
    var list = new List<Message>();

    if (conversation?.Context is not null)
    {
        list.Add(new Message { Id = "context", ConversationId = conversationId, Role = "system", Content = conversation.Context });
    }

    var allMessages = _messages.GetValueOrDefault(conversationId) ?? [];
    var messages = conversation?.CompactedUpToRowId is not null
        ? allMessages.Where(m => m.RowId > conversation.CompactedUpToRowId.Value).ToList()
        : allMessages.ToList();

    if (includeToolResultBlobs)
        PopulateFullToolResults(messages);

    list.AddRange(messages);
    return list.AsReadOnly();
}
```

To:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
{
    var conversation = Get(conversationId);
    var allMessages = _messages.GetValueOrDefault(conversationId) ?? [];
    var messages = conversation?.CompactedUpToRowId is not null
        ? allMessages.Where(m => m.RowId > conversation.CompactedUpToRowId.Value).ToList()
        : allMessages.ToList();

    if (includeToolResultBlobs)
        PopulateFullToolResults(messages);

    return messages.AsReadOnly();
}
```

- [ ] **Step 2: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 3: Commit**

```
refactor(tests): InMemoryConversationStore mirrors GetMessages no-context behavior
```

---

### Task 3: Azure OpenAI provider injects the compaction summary as a user message

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`

- [ ] **Step 1: Inject the summary in `BuildChatMessages`**

Find where the system prompt is added at the top of `BuildChatMessages`:

```csharp
// System prompt
var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type, conversation.ActiveSkills, conversation.Intention);
if (!string.IsNullOrEmpty(systemPrompt))
    chatMessages.Add(new ChatMessage { Role = "system", Content = systemPrompt });

// Reconstruct full message history including tool calls. Opt into blob loading so
// persisted tool results are inlined as their original full content (not the summary).
var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);
```

Add the summary injection between these two blocks:

```csharp
// System prompt
var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type, conversation.ActiveSkills, conversation.Intention);
if (!string.IsNullOrEmpty(systemPrompt))
    chatMessages.Add(new ChatMessage { Role = "system", Content = systemPrompt });

// If the conversation has been compacted, inject the summary as a user message
// wrapped in <summary> tags. This keeps the real system prompt stable across turns
// (cache-friendly) while still giving the model visibility into pre-cut history.
if (!string.IsNullOrEmpty(conversation.Context))
{
    chatMessages.Add(new ChatMessage
    {
        Role = "user",
        Content = "The conversation history before this point was compacted into the following summary:\n\n"
                  + "<summary>\n" + conversation.Context + "\n</summary>"
    });
}

// Reconstruct full message history including tool calls. Opt into blob loading so
// persisted tool results are inlined as their original full content (not the summary).
var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);
```

- [ ] **Step 2: Build and run tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 3: Commit**

```
feat(azure-openai): inject compaction summary as <summary>-wrapped user message
```

---

### Task 4: Anthropic provider injects the compaction summary as a user message

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`

- [ ] **Step 1: Inject the summary in `BuildMessages`**

At the top of `BuildMessages`, before the `for` loop:

```csharp
private List<AnthropicMessage> BuildMessages(Conversation conversation)
{
    var result = new List<AnthropicMessage>();

    // If the conversation has been compacted, inject the summary as a user message
    // wrapped in <summary> tags. Cache-friendly: real system prompt stays stable.
    if (!string.IsNullOrEmpty(conversation.Context))
    {
        result.Add(new AnthropicMessage
        {
            Role = "user",
            Content = "The conversation history before this point was compacted into the following summary:\n\n"
                      + "<summary>\n" + conversation.Context + "\n</summary>"
        });
    }

    // Opt into blob loading so persisted tool results are inlined as their original full
    // content (not the compact summary in Content).
    var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);

    // ... existing for loop unchanged ...
```

- [ ] **Step 2: Build and run tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 3: Commit**

```
feat(anthropic): inject compaction summary as <summary>-wrapped user message
```

---

## Chunk 2: Token estimation utility

Building the token-walk cut point needs a per-message estimator. Keep it simple (chars/4), but split by role so we account for tool-call argument payloads and the new `FullToolResult` content.

### Task 5: Add `TokenEstimator` to `OpenAgent.Compaction`

**Files:**
- Create: `src/agent/OpenAgent.Compaction/TokenEstimator.cs`
- Create: `src/agent/OpenAgent.Tests/TokenEstimatorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `src/agent/OpenAgent.Tests/TokenEstimatorTests.cs`:

```csharp
using OpenAgent.Compaction;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void User_message_estimates_from_content_length()
    {
        var msg = new Message { Id = "m", ConversationId = "c", Role = "user", Content = "Hello, world!" }; // 13 chars
        Assert.Equal(4, TokenEstimator.EstimateMessage(msg)); // ceil(13/4)
    }

    [Fact]
    public void Assistant_with_tool_calls_includes_tool_call_payload()
    {
        var msg = new Message
        {
            Id = "m", ConversationId = "c", Role = "assistant",
            Content = "thinking out loud",       // 17 chars
            ToolCalls = """[{"id":"t1","function":{"name":"read","arguments":"{\"path\":\"x\"}"}}]""" // 60+ chars
        };
        var tokens = TokenEstimator.EstimateMessage(msg);
        Assert.True(tokens >= Math.Ceiling((17 + 60) / 4.0));
    }

    [Fact]
    public void Tool_result_uses_FullToolResult_when_present()
    {
        var shortSummary = """{"tool":"read","status":"ok","size":999999}""";
        var longFull = new string('x', 4000); // 4000 chars
        var msg = new Message
        {
            Id = "m", ConversationId = "c", Role = "tool",
            Content = shortSummary,
            FullToolResult = longFull
        };
        Assert.Equal(1000, TokenEstimator.EstimateMessage(msg)); // 4000/4 = 1000
    }

    [Fact]
    public void Tool_result_falls_back_to_Content_when_FullToolResult_is_null()
    {
        var content = new string('x', 400);
        var msg = new Message { Id = "m", ConversationId = "c", Role = "tool", Content = content };
        Assert.Equal(100, TokenEstimator.EstimateMessage(msg));
    }

    [Fact]
    public void Tool_result_is_capped_to_prevent_single_message_domination()
    {
        var huge = new string('x', 1_000_000); // 250k tokens raw
        var msg = new Message { Id = "m", ConversationId = "c", Role = "tool", FullToolResult = huge };
        var tokens = TokenEstimator.EstimateMessage(msg);
        Assert.True(tokens <= TokenEstimator.ToolResultTokenCap);
    }

    [Fact]
    public void Null_content_returns_zero()
    {
        var msg = new Message { Id = "m", ConversationId = "c", Role = "user", Content = null };
        Assert.Equal(0, TokenEstimator.EstimateMessage(msg));
    }
}
```

Run: `cd src/agent && dotnet test --filter "TokenEstimatorTests"`
Expected: FAIL (class doesn't exist).

- [ ] **Step 2: Implement `TokenEstimator`**

Create `src/agent/OpenAgent.Compaction/TokenEstimator.cs`:

```csharp
using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Conservative character-based token estimator for compaction cut-point logic.
/// Uses a chars/4 heuristic, with per-role specializations and a ceiling on tool
/// results so one huge tool output doesn't dominate the walk.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Approximate tokens per character (inverse of chars/4).</summary>
    private const int CharsPerToken = 4;

    /// <summary>
    /// Maximum tokens a single tool result contributes to the cut-point walk.
    /// Prevents a huge tool output from locking the cut point in place.
    /// </summary>
    public const int ToolResultTokenCap = 12_500; // ~50k chars

    public static int EstimateMessage(Message message)
    {
        var chars = message.Role switch
        {
            "user" => message.Content?.Length ?? 0,
            "assistant" => (message.Content?.Length ?? 0) + (message.ToolCalls?.Length ?? 0),
            "tool" => (message.FullToolResult ?? message.Content)?.Length ?? 0,
            _ => message.Content?.Length ?? 0
        };

        var tokens = (int)Math.Ceiling(chars / (double)CharsPerToken);

        if (message.Role == "tool" && tokens > ToolResultTokenCap)
            tokens = ToolResultTokenCap;

        return tokens;
    }
}
```

- [ ] **Step 3: Run tests — expect pass**

Run: `cd src/agent && dotnet test --filter "TokenEstimatorTests"`
Expected: PASS.

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat: add TokenEstimator for per-message token estimation
```

---

## Chunk 3: Cut point algorithm

### Task 6: Add `KeepRecentTokens` to `CompactionConfig`

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/CompactionConfig.cs`

- [ ] **Step 1: Add `KeepRecentTokens` alongside the existing `KeepLatestMessagePairs`**

```csharp
/// <summary>
/// Target size of the uncompacted tail in tokens. The cut point is the nearest
/// user/assistant boundary (never a tool result) such that the tail estimates
/// to at least this many tokens. Replaces KeepLatestMessagePairs in PR 2.
/// </summary>
public int KeepRecentTokens { get; init; } = 20_000;
```

Leave `KeepLatestMessagePairs` in place for now — Task 8 removes it.

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Success.

- [ ] **Step 3: Commit**

```
feat: add KeepRecentTokens to CompactionConfig
```

---

### Task 7: Add `CompactionCutPoint` helper

**Files:**
- Create: `src/agent/OpenAgent.Compaction/CompactionCutPoint.cs`
- Create: `src/agent/OpenAgent.Tests/CompactionCutPointTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using OpenAgent.Compaction;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class CompactionCutPointTests
{
    private static Message User(string id, string content) =>
        new() { Id = id, ConversationId = "c", Role = "user", Content = content };

    private static Message Asst(string id, string content, string? toolCalls = null) =>
        new() { Id = id, ConversationId = "c", Role = "assistant", Content = content, ToolCalls = toolCalls };

    private static Message Tool(string id, string toolCallId, string full) =>
        new() { Id = id, ConversationId = "c", Role = "tool", Content = "summary", FullToolResult = full, ToolCallId = toolCallId };

    [Fact]
    public void Empty_list_returns_no_cut()
    {
        Assert.Null(CompactionCutPoint.Find([], keepRecentTokens: 100));
    }

    [Fact]
    public void Everything_fits_in_tail_returns_no_cut()
    {
        var messages = new[] { User("u1", "hi"), Asst("a1", "hello") };
        Assert.Null(CompactionCutPoint.Find(messages, keepRecentTokens: 10_000));
    }

    [Fact]
    public void Cut_snaps_to_user_message()
    {
        // User, Asst (short), User, Asst (long — pushes tail over budget)
        var big = new string('x', 400);
        var messages = new[]
        {
            User("u1", new string('x', 80)),
            Asst("a1", "ok"),
            User("u2", "next request"),
            Asst("a2", big)
        };
        var cutIndex = CompactionCutPoint.Find(messages, keepRecentTokens: 50);
        Assert.NotNull(cutIndex);
        Assert.Equal("u2", messages[cutIndex.Value].Id);
    }

    [Fact]
    public void Cut_never_lands_on_tool_result()
    {
        // Force the naive index to land at a tool result; expect snap-back to the assistant or user before it
        var messages = new[]
        {
            User("u1", "do the thing"),
            Asst("a1", "running", toolCalls: """[{"id":"t1","function":{"name":"read","arguments":"{}"}}]"""),
            Tool("tr1", "t1", new string('x', 1000)),
            Asst("a2", "done")
        };
        var cutIndex = CompactionCutPoint.Find(messages, keepRecentTokens: 100);
        Assert.NotNull(cutIndex);
        Assert.NotEqual("tool", messages[cutIndex.Value].Role);
    }

    [Fact]
    public void Cut_keeps_tool_call_and_tool_result_together()
    {
        // The assistant + its tool_result pair should never be split across the cut.
        // With a tiny budget, cut should fall BEFORE the tool-call round, not inside it.
        var messages = new[]
        {
            User("u1", "old question"),
            Asst("a1", "old answer"),
            User("u2", "do tool"),
            Asst("a2", "calling tool", toolCalls: """[{"id":"t1","function":{"name":"read","arguments":"{}"}}]"""),
            Tool("tr1", "t1", "result"),
            Asst("a3", "final")
        };
        var cutIndex = CompactionCutPoint.Find(messages, keepRecentTokens: 30);
        Assert.NotNull(cutIndex);
        // Whatever the cut, the tail must contain either the whole tool-call round or none of it.
        var tail = messages.Skip(cutIndex.Value).ToArray();
        var hasAssistantToolCall = tail.Any(m => m.Role == "assistant" && m.ToolCalls is not null);
        var hasToolResult = tail.Any(m => m.Role == "tool");
        Assert.Equal(hasAssistantToolCall, hasToolResult);
    }

    [Fact]
    public void Voice_conversation_with_short_turns_cuts_at_user_boundary()
    {
        // Many small back-and-forth turns, like a voice transcript.
        var messages = Enumerable.Range(1, 40)
            .SelectMany<int, Message>(i => new[]
            {
                User($"u{i}", $"utterance {i}"),
                Asst($"a{i}", $"reply {i}")
            })
            .ToArray();
        var cutIndex = CompactionCutPoint.Find(messages, keepRecentTokens: 50);
        Assert.NotNull(cutIndex);
        Assert.Equal("user", messages[cutIndex.Value].Role);
    }
}
```

Run: `cd src/agent && dotnet test --filter "CompactionCutPointTests"`
Expected: FAIL.

- [ ] **Step 2: Implement `CompactionCutPoint`**

```csharp
using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Chooses where to split conversation history for compaction. Walks backward from
/// the newest message accumulating estimated tokens until <c>keepRecentTokens</c> is
/// reached, then snaps to the nearest earlier user (or assistant-without-tool_calls)
/// boundary. Never cuts inside a tool-call round — the assistant's tool_calls and
/// its trailing tool result messages stay on the same side of the cut.
/// </summary>
public static class CompactionCutPoint
{
    /// <summary>
    /// Returns the index of the first kept message, or null if no cut is warranted
    /// (i.e. the entire history already fits in <paramref name="keepRecentTokens"/>).
    /// </summary>
    public static int? Find(IReadOnlyList<Message> messages, int keepRecentTokens)
    {
        if (messages.Count == 0) return null;

        // Walk backward, accumulating tokens. Remember the first index at which we
        // cross the budget — we'll snap from there.
        var accumulated = 0;
        var crossIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            accumulated += TokenEstimator.EstimateMessage(messages[i]);
            if (accumulated >= keepRecentTokens)
            {
                crossIndex = i;
                break;
            }
        }

        if (crossIndex < 0) return null; // everything fits

        // Snap to the nearest valid cut boundary at or before crossIndex.
        // Valid boundary: a user message, OR an assistant message with no tool_calls.
        // Tool result messages and assistants-with-tool_calls are invalid.
        for (var i = crossIndex; i >= 0; i--)
        {
            if (IsValidCutBoundary(messages[i]))
                return i;
        }

        return null; // no valid boundary found — don't compact
    }

    private static bool IsValidCutBoundary(Message msg) =>
        msg.Role switch
        {
            "user" => true,
            "assistant" => string.IsNullOrEmpty(msg.ToolCalls),
            _ => false
        };
}
```

- [ ] **Step 3: Run tests — expect pass**

Run: `cd src/agent && dotnet test --filter "CompactionCutPointTests"`
Expected: PASS.

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat: add CompactionCutPoint for token-aware boundary-safe cuts
```

---

### Task 8: Refactor `RunCompactionAsync` to use `CompactionCutPoint`

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Update `RunCompactionAsync` to use the new cut point**

Change:

```csharp
private async Task RunCompactionAsync(Conversation conversation)
{
    var liveMessages = ReadMessagesFromDb(conversation.Id, conversation.CompactedUpToRowId);

    var keepCount = _compactionConfig.KeepLatestMessagePairs * 2;
    if (liveMessages.Count <= keepCount)
    {
        _logger.LogDebug("Not enough messages to compact for conversation {ConversationId}", conversation.Id);
        return;
    }

    var toCompact = liveMessages.GetRange(0, liveMessages.Count - keepCount);
    var newCutoffRowId = toCompact[^1].RowId;
    // ...
}
```

To:

```csharp
private async Task RunCompactionAsync(Conversation conversation)
{
    // Load full tool result content so the summarizer sees what actually happened,
    // not the compact stubs in Content.
    var liveMessages = ReadMessagesFromDb(conversation.Id, conversation.CompactedUpToRowId);
    LoadToolResultBlobs(conversation.Id, liveMessages);

    var cutIndex = CompactionCutPoint.Find(liveMessages, _compactionConfig.KeepRecentTokens);
    if (cutIndex is null)
    {
        _logger.LogDebug("Nothing to compact for conversation {ConversationId} — history fits in {Budget} tokens",
            conversation.Id, _compactionConfig.KeepRecentTokens);
        return;
    }

    var toCompact = liveMessages.GetRange(0, cutIndex.Value);
    if (toCompact.Count == 0) return;

    var newCutoffRowId = toCompact[^1].RowId;

    _logger.LogInformation("Compacting {Count} messages for conversation {ConversationId}, cutoff rowid {RowId}",
        toCompact.Count, conversation.Id, newCutoffRowId);

    var result = await _compactionSummarizer!.SummarizeAsync(conversation.Context, toCompact);

    UpdateCompactionState(conversation.Id, compactionRunning: false, context: result.Context, compactedUpToRowId: newCutoffRowId);

    _logger.LogInformation("Compaction complete for conversation {ConversationId}, context length {Length} chars",
        conversation.Id, result.Context.Length);
}
```

Add `using OpenAgent.Compaction;` if not already present.

- [ ] **Step 2: Update the existing compaction test to use `KeepRecentTokens`**

In `Compaction_summarizes_old_messages_and_updates_cutoff`, change the config:

```csharp
var config = new CompactionConfig
{
    MaxContextTokens = 100,
    CompactionTriggerPercent = 50,
    KeepRecentTokens = 20 // tiny budget to force a cut after the first two short messages
};
```

The test previously used `KeepLatestMessagePairs = 1`; now it uses `KeepRecentTokens = 20`. Verify the assertions still hold (cut happens, summary is generated, tail contains msg5/msg6).

- [ ] **Step 3: Run tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 4: Commit**

```
refactor(compaction): use CompactionCutPoint with token budget
```

---

### Task 9: Remove `KeepLatestMessagePairs`

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/CompactionConfig.cs`

- [ ] **Step 1: Delete `KeepLatestMessagePairs` from `CompactionConfig`**

Remove the property entirely. Any remaining test usage was replaced in Task 8.

- [ ] **Step 2: Build + run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 3: Commit**

```
refactor: remove KeepLatestMessagePairs in favor of KeepRecentTokens
```

---

## Chunk 4: Per-model context window

### Task 10: Add `Conversation.ContextWindowTokens`

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Conversation.cs`
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Add the property**

In `Conversation.cs`, near `LastPromptTokens`:

```csharp
/// <summary>
/// Cached context window size for the active model, populated by providers lazily.
/// Null on new conversations or after a model switch invalidates the cache.
/// </summary>
[JsonPropertyName("context_window_tokens")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public int? ContextWindowTokens { get; set; }
```

- [ ] **Step 2: Add the SQLite column migration**

In `InitializeDatabase`, alongside the other `TryAddColumn` calls:

```csharp
TryAddColumn(connection, "Conversations", "ContextWindowTokens", "INTEGER");
```

- [ ] **Step 3: Update `Conversation` SELECTs and `ReadConversation`**

Add `ContextWindowTokens` to the four SELECTs (`FindChannelConversation`, `GetAll`, `Get`) — append it at the end of the column list. Update `ReadConversation`:

```csharp
ContextWindowTokens = reader.IsDBNull(22) ? null : reader.GetInt32(22)
```

(Position 22 is the next free index after `Intention` at position 21.)

- [ ] **Step 4: Update `Update` INSERT**

Add `ContextWindowTokens = @contextWindowTokens` to the UPDATE SET clause and:

```csharp
cmd.Parameters.AddWithValue("@contextWindowTokens", (object?)conversation.ContextWindowTokens ?? DBNull.Value);
```

- [ ] **Step 5: Write test**

```csharp
[Fact]
public void ContextWindowTokens_persists_and_round_trips()
{
    var conv = _store.GetOrCreate("conv1", "test", ConversationType.Text, "p", "m");
    conv.ContextWindowTokens = 200_000;
    _store.Update(conv);

    var refreshed = _store.Get("conv1");
    Assert.NotNull(refreshed);
    Assert.Equal(200_000, refreshed.ContextWindowTokens);
}
```

- [ ] **Step 6: Run tests**

Run: `cd src/agent && dotnet test --filter "ContextWindowTokens_persists_and_round_trips"`
Expected: PASS.

- [ ] **Step 7: Commit**

```
feat: add Conversation.ContextWindowTokens with schema migration
```

---

### Task 11: Add `ILlmTextProvider.GetContextWindow`

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/ILlmTextProvider.cs`
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`

- [ ] **Step 1: Add the method to the interface**

```csharp
/// <summary>
/// Returns the context window size in tokens for the given model, or null if the
/// provider cannot determine it (e.g. unknown model, misconfiguration). Callers
/// fall back to <see cref="CompactionConfig.MaxContextTokens"/>.
/// </summary>
int? GetContextWindow(string model);
```

- [ ] **Step 2: Implement in Azure OpenAI**

Azure models all share large windows on modern deployments. Return a sensible default per recognized model, or fall back to null for unknowns:

```csharp
public int? GetContextWindow(string model)
{
    // Azure deployment names are user-chosen, so this is a best-effort table.
    // Update as new models are deployed. Unknown models return null.
    if (model.Contains("gpt-5", StringComparison.OrdinalIgnoreCase)) return 400_000;
    if (model.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase)) return 128_000;
    if (model.Contains("gpt-4", StringComparison.OrdinalIgnoreCase)) return 128_000;
    return null;
}
```

- [ ] **Step 3: Implement in Anthropic**

```csharp
public int? GetContextWindow(string model)
{
    // Anthropic model IDs encode the family; most modern Claude variants expose
    // 200k context. Update if a smaller-context model becomes supported.
    if (model.Contains("claude", StringComparison.OrdinalIgnoreCase)) return 200_000;
    return null;
}
```

- [ ] **Step 4: Build**

Run: `cd src/agent && dotnet build`
Expected: Success.

- [ ] **Step 5: Commit**

```
feat: add ILlmTextProvider.GetContextWindow and implement for both providers
```

---

### Task 12: Populate `ContextWindowTokens` in providers; use it in the threshold

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Populate `ContextWindowTokens` when null or when the model changes**

In each provider's `CompleteAsync(Conversation, Message, ...)`, after `agentLogic.AddMessage(conversationId, userMessage);` and before building messages, populate the window:

```csharp
// Populate per-conversation context window cache (used by compaction threshold)
if (conversation.ContextWindowTokens is null)
{
    var window = GetContextWindow(conversation.Model);
    if (window is not null)
        conversation.ContextWindowTokens = window;
}
```

The cached value is persisted whenever the provider's existing `agentLogic.UpdateConversation(fresh)` call runs at end-of-turn. No separate UPDATE needed.

Add the same block to both providers.

- [ ] **Step 2: Use the per-conversation window in `TryStartCompaction`**

Change:

```csharp
if (conversation.LastPromptTokens.Value < _compactionConfig.TriggerThreshold) return;
```

To:

```csharp
var window = conversation.ContextWindowTokens ?? _compactionConfig.MaxContextTokens;
var triggerThreshold = window * _compactionConfig.CompactionTriggerPercent / 100;
if (conversation.LastPromptTokens.Value < triggerThreshold) return;
```

`CompactionConfig.TriggerThreshold` (the computed property) can stay as a fallback helper but is no longer the source of truth — callers should prefer the per-conversation window.

- [ ] **Step 3: Write test for threshold scaling**

```csharp
[Fact]
public void Compaction_uses_per_conversation_ContextWindowTokens_for_threshold()
{
    var config = new CompactionConfig
    {
        MaxContextTokens = 1_000_000,  // fallback shouldn't fire
        CompactionTriggerPercent = 50,
        KeepRecentTokens = 10
    };
    var summarizer = new FakeCompactionSummarizer("summary");
    var env = new AgentEnvironment { DataPath = _dbDir };
    using var store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance, config, summarizer);

    var conv = store.GetOrCreate("conv1", "test", ConversationType.Text, "p", "m");
    conv.ContextWindowTokens = 100; // tiny per-conversation window → trigger = 50
    store.Update(conv);

    store.AddMessage("conv1", new Message { Id = "u1", ConversationId = "conv1", Role = "user", Content = "hi" });
    store.AddMessage("conv1", new Message { Id = "a1", ConversationId = "conv1", Role = "assistant", Content = "hello" });
    store.AddMessage("conv1", new Message { Id = "u2", ConversationId = "conv1", Role = "user", Content = "more" });
    store.AddMessage("conv1", new Message { Id = "a2", ConversationId = "conv1", Role = "assistant", Content = "ok" });

    // LastPromptTokens = 60 → over per-conv threshold (50), under fallback threshold (500k)
    var fresh = store.Get("conv1")!;
    fresh.LastPromptTokens = 60;
    store.Update(fresh);

    await Task.Delay(500); // wait for background compaction

    Assert.NotNull(summarizer.LastMessages);
}
```

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat: drive compaction threshold from per-conversation ContextWindowTokens
```

---

## Verification

- [ ] **Step 1: Final full test run**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 2: Manual smoke check — summary no longer in UI**

Start the agent against a conversation that has been compacted (from the test data or a fresh run). Via the REST API:

- `GET /api/conversations/{id}/messages` returns only post-cut messages — no `system` role entry for the compaction summary.
- `GET /api/conversations/{id}` returns the conversation with `context` populated (the summary is still there, just not in the message list).

- [ ] **Step 3: Manual smoke check — provider sees summary**

Enable request logging (or use a debugger). Submit a turn on a compacted conversation and verify:

- Azure: the payload to `/chat/completions` contains a `user` message with `Content` starting with `"The conversation history before this point was compacted into the following summary:\n\n<summary>\n..."`.
- Anthropic: the payload to `v1/messages` contains an equivalent `user` message.

In both cases, the real system prompt is the first entry (Azure) or the `system` block (Anthropic) and has not changed shape.

- [ ] **Step 4: Manual smoke check — threshold scales with model**

Swap `AgentConfig.TextModel` between a known Azure deployment and an Anthropic model, then force compaction by sending a long conversation. Verify (via logs) that the trigger threshold reported matches the per-model window returned by `GetContextWindow`, not the fallback 400k.

---

## Out of Scope (Covered by PR 3)

- Overflow-triggered compaction with auto-retry.
- Manual `POST /api/conversations/{id}/compact` endpoint.
- Split compaction prompt into `Initial` / `Update` (iterative summary).
- Memory-retrieval preservation rule in the prompt (requires prompt split).
- Per-conversation `CancellationTokenSource` / proper cancellation plumbing.
- Structured `compaction.start` / `compaction.complete` / `compaction.error` log events.
- Error handling when `CompactionProvider` / `CompactionModel` are unset (latent bug: compaction fails silently in a retry loop — PR 3 should surface this or skip compaction entirely).
