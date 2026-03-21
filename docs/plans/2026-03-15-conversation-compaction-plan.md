# Conversation Compaction Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Compress old conversation messages into a structured summary with message references, keeping originals for on-demand retrieval.

**Architecture:** Compaction lives inside `OpenAgent.ConversationStore.Sqlite`. When `Update()` detects `LastPromptTokens` exceeding the threshold, it spawns a background compaction that summarizes older messages via an injected `ICompactionSummarizer`, updates `Conversation.Context` and `CompactedUpToRowId`, and leaves original messages untouched. `GetMessages()` returns only live messages, prepending the summary.

**Tech Stack:** .NET 10, SQLite, xUnit, existing Azure OpenAI text provider for summarization LLM calls.

**Spec:** `docs/plans/2026-03-15-conversation-compaction.md`

---

## Chunk 1: Data Model and Query Changes

### Task 1: Add RowId to Message model

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Message.cs`

- [ ] **Step 1: Add RowId property to Message**

```csharp
/// <summary>
/// SQLite rowid — populated by the store, not persisted via INSERT.
/// Used for compaction boundary tracking.
/// </summary>
[JsonPropertyName("row_id")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
public long RowId { get; init; }
```

Add after the `ReplyToChannelMessageId` property.

- [ ] **Step 2: Build to verify**

Run: `cd src/agent && dotnet build`
Expected: Success

- [ ] **Step 3: Commit**

```
feat: add RowId property to Message model for compaction tracking
```

---

### Task 2: Update SQLite queries to SELECT rowid

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Write a test that verifies RowId is populated**

Create file: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

```csharp
using OpenAgent.ConversationStore.Sqlite;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAgent.Tests;

public class SqliteConversationStoreTests : IDisposable
{
    private readonly string _dbDir;
    private readonly SqliteConversationStore _store;

    public SqliteConversationStoreTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"openagent-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbDir);
        var env = new AgentEnvironment(_dbDir);
        _store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    [Fact]
    public void GetMessages_populates_RowId()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text);

        _store.AddMessage("conv1", new Message
        {
            Id = "msg1", ConversationId = "conv1", Role = "user", Content = "hello"
        });
        _store.AddMessage("conv1", new Message
        {
            Id = "msg2", ConversationId = "conv1", Role = "assistant", Content = "hi"
        });

        var messages = _store.GetMessages("conv1");

        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].RowId > 0, "First message should have a positive RowId");
        Assert.True(messages[1].RowId > messages[0].RowId, "Second message RowId should be greater");
    }
}
```

- [ ] **Step 2: Run test — expect it to fail**

Run: `cd src/agent && dotnet test --filter "GetMessages_populates_RowId"`
Expected: FAIL — RowId is 0 because the query doesn't select rowid.

- [ ] **Step 3: Update GetMessages query to include rowid**

In `SqliteConversationStore.cs`, update the `GetMessages` method. Change the query from:

```csharp
cmd.CommandText = "SELECT Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId FROM Messages WHERE ConversationId = @id ORDER BY CreatedAt";
```

To:

```csharp
cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId FROM Messages WHERE ConversationId = @id ORDER BY rowid";
```

Update the reader indices — all shift by 1 since rowid is now index 0:

```csharp
list.Add(new Message
{
    RowId = reader.GetInt64(0),
    Id = reader.GetString(1),
    ConversationId = reader.GetString(2),
    Role = reader.GetString(3),
    Content = reader.IsDBNull(4) ? null : reader.GetString(4),
    CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
    ToolCalls = reader.IsDBNull(6) ? null : reader.GetString(6),
    ToolCallId = reader.IsDBNull(7) ? null : reader.GetString(7),
    ChannelMessageId = reader.IsDBNull(8) ? null : reader.GetString(8),
    ReplyToChannelMessageId = reader.IsDBNull(9) ? null : reader.GetString(9)
});
```

Also change `ORDER BY CreatedAt` to `ORDER BY rowid` — rowid is the true insertion order and is indexed.

- [ ] **Step 4: Run test — expect it to pass**

Run: `cd src/agent && dotnet test --filter "GetMessages_populates_RowId"`
Expected: PASS

- [ ] **Step 5: Run all tests to verify nothing broke**

Run: `cd src/agent && dotnet test`
Expected: All pass

- [ ] **Step 6: Commit**

```
feat: select rowid in GetMessages and populate Message.RowId
```

---

### Task 3: Add compaction fields to Conversation model

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Conversation.cs`

- [ ] **Step 1: Add Context, CompactedUpToRowId, CompactionRunning to Conversation**

Add after `LastPromptTokens`:

```csharp
/// <summary>
/// Structured summary of compacted messages — topic-grouped with timestamps and message references.
/// Null until the first compaction runs.
/// </summary>
[JsonPropertyName("context")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? Context { get; set; }

/// <summary>
/// SQLite rowid of the last message included in the compaction summary.
/// Messages with rowid > this value are live; messages up to and including it are compacted.
/// Null means no compaction has occurred.
/// </summary>
[JsonPropertyName("compacted_up_to_row_id")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public long? CompactedUpToRowId { get; set; }

/// <summary>
/// True while a compaction thread is running — prevents concurrent compaction.
/// </summary>
[JsonPropertyName("compaction_running")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
public bool CompactionRunning { get; set; }
```

- [ ] **Step 2: Build to verify**

Run: `cd src/agent && dotnet build`
Expected: Success

- [ ] **Step 3: Commit**

```
feat: add Context, CompactedUpToRowId, CompactionRunning to Conversation model
```

---

### Task 4: Add SQLite schema migration for compaction fields

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Add TryAddColumn calls for new Conversation columns**

In `InitializeDatabase()`, after the existing `TryAddColumn` calls, add:

```csharp
TryAddColumn(connection, "Conversations", "Context", "TEXT");
TryAddColumn(connection, "Conversations", "CompactedUpToRowId", "INTEGER");
TryAddColumn(connection, "Conversations", "CompactionRunning", "INTEGER NOT NULL DEFAULT 0");
```

- [ ] **Step 2: Update ReadConversation to read new columns**

Update the SELECT in `Get()` and `GetAll()` to include the new columns. Change:

```csharp
"SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens FROM Conversations"
```

To:

```csharp
"SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning FROM Conversations"
```

Update `ReadConversation`:

```csharp
private static Conversation ReadConversation(SqliteDataReader reader)
{
    return new Conversation
    {
        Id = reader.GetString(0),
        Source = reader.GetString(1),
        Type = (ConversationType)reader.GetInt32(2),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(3)),
        VoiceSessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
        VoiceSessionOpen = reader.GetInt32(5) != 0,
        LastPromptTokens = reader.IsDBNull(6) ? null : reader.GetInt32(6),
        Context = reader.IsDBNull(7) ? null : reader.GetString(7),
        CompactedUpToRowId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
        CompactionRunning = !reader.IsDBNull(9) && reader.GetInt32(9) != 0
    };
}
```

- [ ] **Step 3: Update the Update method to persist new columns**

Change the UPDATE SQL in `Update()`:

```csharp
cmd.CommandText = """
    UPDATE Conversations
    SET Source = @source, Type = @type, VoiceSessionId = @voiceSessionId,
        VoiceSessionOpen = @voiceSessionOpen, LastPromptTokens = @lastPromptTokens,
        Context = @context, CompactedUpToRowId = @compactedUpToRowId,
        CompactionRunning = @compactionRunning
    WHERE Id = @id
    """;
```

Add the new parameters:

```csharp
cmd.Parameters.AddWithValue("@context", (object?)conversation.Context ?? DBNull.Value);
cmd.Parameters.AddWithValue("@compactedUpToRowId", (object?)conversation.CompactedUpToRowId ?? DBNull.Value);
cmd.Parameters.AddWithValue("@compactionRunning", conversation.CompactionRunning ? 1 : 0);
```

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass

- [ ] **Step 5: Commit**

```
feat: add compaction columns to SQLite schema with migration
```

---

### Task 5: Update GetMessages to filter by CompactedUpToRowId and prepend Context

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Write test — GetMessages excludes compacted messages**

Add to `SqliteConversationStoreTests.cs`:

```csharp
[Fact]
public void GetMessages_excludes_compacted_messages()
{
    var conv = _store.GetOrCreate("conv1", "test", ConversationType.Text);

    _store.AddMessage("conv1", new Message { Id = "msg1", ConversationId = "conv1", Role = "user", Content = "old message" });
    _store.AddMessage("conv1", new Message { Id = "msg2", ConversationId = "conv1", Role = "assistant", Content = "old reply" });
    _store.AddMessage("conv1", new Message { Id = "msg3", ConversationId = "conv1", Role = "user", Content = "new message" });

    // Get the rowid of msg2 so we can set the cutoff
    var allMessages = _store.GetMessages("conv1");
    var cutoffRowId = allMessages[1].RowId; // msg2

    // Set compaction cutoff
    conv.CompactedUpToRowId = cutoffRowId;
    conv.Context = "## Summary\nOld conversation about greetings.\n[ref: msg1, msg2]";
    _store.Update(conv);

    // Now GetMessages should only return msg3 plus the context
    var messages = _store.GetMessages("conv1");

    Assert.Equal(2, messages.Count);
    Assert.Equal("system", messages[0].Role);
    Assert.Contains("Old conversation about greetings", messages[0].Content);
    Assert.Equal("msg3", messages[1].Id);
}
```

- [ ] **Step 2: Write test — GetMessages returns all when no compaction**

```csharp
[Fact]
public void GetMessages_returns_all_when_no_compaction()
{
    _store.GetOrCreate("conv1", "test", ConversationType.Text);

    _store.AddMessage("conv1", new Message { Id = "msg1", ConversationId = "conv1", Role = "user", Content = "hello" });
    _store.AddMessage("conv1", new Message { Id = "msg2", ConversationId = "conv1", Role = "assistant", Content = "hi" });

    var messages = _store.GetMessages("conv1");

    Assert.Equal(2, messages.Count);
    Assert.Equal("msg1", messages[0].Id);
    Assert.Equal("msg2", messages[1].Id);
}
```

- [ ] **Step 3: Run tests — expect failures**

Run: `cd src/agent && dotnet test --filter "SqliteConversationStoreTests"`
Expected: `GetMessages_excludes_compacted_messages` FAILS (no filtering yet).

- [ ] **Step 4: Update GetMessages to filter and prepend context**

Update `GetMessages` in `SqliteConversationStore.cs`:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId)
{
    // Load conversation to check compaction state
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

    using var connection = Open();
    using var cmd = connection.CreateCommand();

    // Filter out compacted messages if a cutoff exists
    if (conversation?.CompactedUpToRowId is not null)
    {
        cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId FROM Messages WHERE ConversationId = @id AND rowid > @cutoff ORDER BY rowid";
        cmd.Parameters.AddWithValue("@cutoff", conversation.CompactedUpToRowId.Value);
    }
    else
    {
        cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId FROM Messages WHERE ConversationId = @id ORDER BY rowid";
    }
    cmd.Parameters.AddWithValue("@id", conversationId);

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        list.Add(new Message
        {
            RowId = reader.GetInt64(0),
            Id = reader.GetString(1),
            ConversationId = reader.GetString(2),
            Role = reader.GetString(3),
            Content = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            ToolCalls = reader.IsDBNull(6) ? null : reader.GetString(6),
            ToolCallId = reader.IsDBNull(7) ? null : reader.GetString(7),
            ChannelMessageId = reader.IsDBNull(8) ? null : reader.GetString(8),
            ReplyToChannelMessageId = reader.IsDBNull(9) ? null : reader.GetString(9)
        });
    }

    return list;
}
```

- [ ] **Step 5: Run tests — expect pass**

Run: `cd src/agent && dotnet test --filter "SqliteConversationStoreTests"`
Expected: All pass

- [ ] **Step 6: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass

- [ ] **Step 7: Commit**

```
feat: GetMessages filters by CompactedUpToRowId and prepends context summary
```

---

### Task 6: Add GetMessagesByIds for the expand tool

The expand tool needs to retrieve specific messages by their IDs, regardless of compaction state.

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/IConversationStore.cs`
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Modify: `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Add GetMessagesByIds to IConversationStore**

```csharp
/// <summary>Returns messages by their IDs, regardless of compaction state. Used by the expand tool.</summary>
IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds);
```

- [ ] **Step 2: Write test**

Add to `SqliteConversationStoreTests.cs`:

```csharp
[Fact]
public void GetMessagesByIds_returns_compacted_messages()
{
    var conv = _store.GetOrCreate("conv1", "test", ConversationType.Text);

    _store.AddMessage("conv1", new Message { Id = "msg1", ConversationId = "conv1", Role = "user", Content = "old" });
    _store.AddMessage("conv1", new Message { Id = "msg2", ConversationId = "conv1", Role = "assistant", Content = "old reply" });
    _store.AddMessage("conv1", new Message { Id = "msg3", ConversationId = "conv1", Role = "user", Content = "new" });

    // Compact first two messages
    var allMessages = _store.GetMessages("conv1");
    conv.CompactedUpToRowId = allMessages[1].RowId;
    conv.Context = "Summary";
    _store.Update(conv);

    // GetMessagesByIds should still return compacted messages
    var result = _store.GetMessagesByIds(["msg1", "msg2"]);

    Assert.Equal(2, result.Count);
    Assert.Equal("old", result[0].Content);
    Assert.Equal("old reply", result[1].Content);
}
```

- [ ] **Step 3: Run test — expect failure**

Run: `cd src/agent && dotnet test --filter "GetMessagesByIds_returns_compacted_messages"`
Expected: FAIL — method doesn't exist yet.

- [ ] **Step 4: Implement in SqliteConversationStore**

```csharp
public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds)
{
    if (messageIds.Count == 0) return [];

    using var connection = Open();
    using var cmd = connection.CreateCommand();

    // Build parameterized IN clause
    var paramNames = messageIds.Select((_, i) => $"@id{i}").ToList();
    cmd.CommandText = $"SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId FROM Messages WHERE Id IN ({string.Join(", ", paramNames)}) ORDER BY rowid";

    for (var i = 0; i < messageIds.Count; i++)
        cmd.Parameters.AddWithValue(paramNames[i], messageIds[i]);

    using var reader = cmd.ExecuteReader();
    var list = new List<Message>();
    while (reader.Read())
    {
        list.Add(new Message
        {
            RowId = reader.GetInt64(0),
            Id = reader.GetString(1),
            ConversationId = reader.GetString(2),
            Role = reader.GetString(3),
            Content = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            ToolCalls = reader.IsDBNull(6) ? null : reader.GetString(6),
            ToolCallId = reader.IsDBNull(7) ? null : reader.GetString(7),
            ChannelMessageId = reader.IsDBNull(8) ? null : reader.GetString(8),
            ReplyToChannelMessageId = reader.IsDBNull(9) ? null : reader.GetString(9)
        });
    }

    return list;
}
```

- [ ] **Step 5: Implement in InMemoryConversationStore**

```csharp
public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds)
{
    var idSet = messageIds.ToHashSet();
    return _messages.Values
        .SelectMany(msgs => msgs)
        .Where(m => idSet.Contains(m.Id))
        .ToList();
}
```

- [ ] **Step 6: Run tests**

Run: `cd src/agent && dotnet test`
Expected: All pass

- [ ] **Step 7: Commit**

```
feat: add GetMessagesByIds for retrieving compacted messages by ID
```

---

## Chunk 2: Compaction Configuration and Logic

### Task 7: Add compaction configuration model

**Files:**
- Create: `src/agent/OpenAgent.Models/Conversations/CompactionConfig.cs`

- [ ] **Step 1: Create CompactionConfig**

```csharp
namespace OpenAgent.Models.Conversations;

/// <summary>
/// Configuration for conversation compaction thresholds.
/// </summary>
public sealed class CompactionConfig
{
    /// <summary>Model's context window size in tokens.</summary>
    public int MaxContextTokens { get; init; } = 400_000;

    /// <summary>Trigger compaction at this percentage of MaxContextTokens.</summary>
    public int CompactionTriggerPercent { get; init; } = 70;

    /// <summary>Number of recent message pairs to keep uncompacted.</summary>
    public int KeepLatestMessagePairs { get; init; } = 5;

    /// <summary>Computed trigger threshold in tokens.</summary>
    public int TriggerThreshold => MaxContextTokens * CompactionTriggerPercent / 100;
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Success

- [ ] **Step 3: Commit**

```
feat: add CompactionConfig model
```

---

### Task 8: Define ICompactionSummarizer interface

The SQLite store needs to call an LLM to generate summaries, but shouldn't depend on a specific LLM provider. Define an interface in Contracts.

**Files:**
- Create: `src/agent/OpenAgent.Contracts/ICompactionSummarizer.cs`

- [ ] **Step 1: Create interface**

```csharp
using OpenAgent.Models.Conversations;

namespace OpenAgent.Contracts;

/// <summary>
/// Generates a compaction summary from conversation messages.
/// Called by the conversation store when compaction is triggered.
/// </summary>
public interface ICompactionSummarizer
{
    /// <summary>
    /// Summarizes messages into a structured context with topic grouping, timestamps, and message references.
    /// </summary>
    /// <param name="existingContext">Previous compaction summary to roll into the new one, or null.</param>
    /// <param name="messages">Messages to compact — includes user, assistant, and tool call messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new structured summary to store as Conversation.Context.</returns>
    Task<CompactionResult> SummarizeAsync(string? existingContext, IReadOnlyList<Message> messages, CancellationToken ct = default);
}

/// <summary>
/// Result of a compaction summarization.
/// </summary>
public sealed class CompactionResult
{
    /// <summary>Structured summary with topic grouping, timestamps, and [ref: ...] message references.</summary>
    public required string Context { get; init; }

    /// <summary>Durable facts extracted for daily memory, if any.</summary>
    public IReadOnlyList<string> Memories { get; init; } = [];
}
```

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Success

- [ ] **Step 3: Commit**

```
feat: add ICompactionSummarizer interface for LLM-driven compaction
```

---

### Task 9: Add a method to retrieve live messages for compaction

The compaction logic needs to read live messages (after cutoff) *without* the context prepended — it needs the raw messages to decide what to compact. Add a separate internal method.

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Extract message-reading SQL into a helper**

Create a private method `ReadMessagesFromDb` that reads messages with an optional rowid cutoff. This is used by both `GetMessages` (public, prepends context) and `GetLiveMessages` (internal, raw messages for compaction).

```csharp
/// <summary>Reads messages from the database, optionally filtering by rowid cutoff.</summary>
private List<Message> ReadMessagesFromDb(string conversationId, long? afterRowId = null)
{
    using var connection = Open();
    using var cmd = connection.CreateCommand();

    if (afterRowId is not null)
    {
        cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId FROM Messages WHERE ConversationId = @id AND rowid > @cutoff ORDER BY rowid";
        cmd.Parameters.AddWithValue("@cutoff", afterRowId.Value);
    }
    else
    {
        cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId FROM Messages WHERE ConversationId = @id ORDER BY rowid";
    }
    cmd.Parameters.AddWithValue("@id", conversationId);

    using var reader = cmd.ExecuteReader();
    var list = new List<Message>();
    while (reader.Read())
    {
        list.Add(new Message
        {
            RowId = reader.GetInt64(0),
            Id = reader.GetString(1),
            ConversationId = reader.GetString(2),
            Role = reader.GetString(3),
            Content = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            ToolCalls = reader.IsDBNull(6) ? null : reader.GetString(6),
            ToolCallId = reader.IsDBNull(7) ? null : reader.GetString(7),
            ChannelMessageId = reader.IsDBNull(8) ? null : reader.GetString(8),
            ReplyToChannelMessageId = reader.IsDBNull(9) ? null : reader.GetString(9)
        });
    }

    return list;
}
```

Refactor `GetMessages` to use this helper:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId)
{
    var conversation = Get(conversationId);
    var list = new List<Message>();

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
    return list;
}
```

- [ ] **Step 2: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass (refactor only, behavior unchanged)

- [ ] **Step 3: Commit**

```
refactor: extract ReadMessagesFromDb helper in SQLite store
```

---

### Task 10: Implement compaction logic

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Add ICompactionSummarizer and CompactionConfig as constructor dependencies**

Update the constructor:

```csharp
public sealed class SqliteConversationStore(
    AgentEnvironment environment,
    ILogger<SqliteConversationStore> logger,
    CompactionConfig compactionConfig,
    ICompactionSummarizer? compactionSummarizer = null) : IConversationStore, IDisposable
```

The summarizer is optional — if not registered, compaction is disabled. Store `compactionConfig` and `compactionSummarizer` as fields.

Add required usings:

```csharp
using OpenAgent.Models.Conversations;
```

(CompactionConfig is in this namespace)

- [ ] **Step 2: Add TryStartCompaction method**

```csharp
/// <summary>
/// Checks if compaction should run and starts it in the background if so.
/// Called from Update() when LastPromptTokens is set.
/// </summary>
private void TryStartCompaction(Conversation conversation)
{
    if (compactionSummarizer is null) return;
    if (conversation.CompactionRunning) return;
    if (conversation.LastPromptTokens is null) return;
    if (conversation.LastPromptTokens.Value < compactionConfig.TriggerThreshold) return;

    // Set lock
    conversation.CompactionRunning = true;
    UpdateCompactionState(conversation.Id, compactionRunning: true, context: null, compactedUpToRowId: null);

    // Fire and forget — compaction runs in the background
    _ = Task.Run(async () =>
    {
        try
        {
            await RunCompactionAsync(conversation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Compaction failed for conversation {ConversationId}", conversation.Id);
        }
        finally
        {
            UpdateCompactionState(conversation.Id, compactionRunning: false, context: null, compactedUpToRowId: null);
        }
    });
}
```

- [ ] **Step 3: Add RunCompactionAsync method**

```csharp
private async Task RunCompactionAsync(Conversation conversation)
{
    // Read live messages (after current cutoff)
    var liveMessages = ReadMessagesFromDb(conversation.Id, conversation.CompactedUpToRowId);

    // Keep the latest N pairs — count from the end
    var keepCount = compactionConfig.KeepLatestMessagePairs * 2;
    if (liveMessages.Count <= keepCount)
    {
        logger.LogDebug("Not enough messages to compact for conversation {ConversationId}", conversation.Id);
        return;
    }

    var toCompact = liveMessages.GetRange(0, liveMessages.Count - keepCount);
    var newCutoffRowId = toCompact[^1].RowId;

    logger.LogInformation("Compacting {Count} messages for conversation {ConversationId}, cutoff rowid {RowId}",
        toCompact.Count, conversation.Id, newCutoffRowId);

    // Call the summarizer
    var result = await compactionSummarizer!.SummarizeAsync(conversation.Context, toCompact);

    // Update conversation state atomically
    UpdateCompactionState(conversation.Id, compactionRunning: false, context: result.Context, compactedUpToRowId: newCutoffRowId);

    logger.LogInformation("Compaction complete for conversation {ConversationId}, context length {Length} chars",
        conversation.Id, result.Context.Length);
}
```

- [ ] **Step 4: Add UpdateCompactionState helper**

```csharp
/// <summary>Updates compaction-related fields on a conversation. Null values mean "don't change".</summary>
private void UpdateCompactionState(string conversationId, bool compactionRunning, string? context, long? compactedUpToRowId)
{
    using var connection = Open();
    using var cmd = connection.CreateCommand();

    var setClauses = new List<string> { "CompactionRunning = @running" };
    cmd.Parameters.AddWithValue("@running", compactionRunning ? 1 : 0);

    if (context is not null)
    {
        setClauses.Add("Context = @context");
        cmd.Parameters.AddWithValue("@context", context);
    }

    if (compactedUpToRowId is not null)
    {
        setClauses.Add("CompactedUpToRowId = @cutoff");
        cmd.Parameters.AddWithValue("@cutoff", compactedUpToRowId.Value);
    }

    cmd.CommandText = $"UPDATE Conversations SET {string.Join(", ", setClauses)} WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", conversationId);
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 5: Call TryStartCompaction from Update()**

At the end of the existing `Update` method, add:

```csharp
TryStartCompaction(conversation);
```

- [ ] **Step 6: Write compaction integration test**

Add to `SqliteConversationStoreTests.cs`:

```csharp
[Fact]
public async Task Compaction_summarizes_old_messages_and_updates_cutoff()
{
    // Create store with a fake summarizer and low threshold
    var config = new CompactionConfig
    {
        MaxContextTokens = 100,
        CompactionTriggerPercent = 50, // trigger at 50 tokens
        KeepLatestMessagePairs = 1
    };
    var summarizer = new FakeCompactionSummarizer("## Summary\nTest summary.\n[ref: msg1, msg2, msg3, msg4]");
    var env = new AgentEnvironment(_dbDir);
    using var store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance, config, summarizer);

    var conv = store.GetOrCreate("conv1", "test", ConversationType.Text);

    // Add enough messages to trigger compaction
    for (var i = 1; i <= 6; i++)
    {
        store.AddMessage("conv1", new Message
        {
            Id = $"msg{i}", ConversationId = "conv1",
            Role = i % 2 == 1 ? "user" : "assistant",
            Content = $"message {i}"
        });
    }

    // Trigger compaction by setting LastPromptTokens above threshold
    conv.LastPromptTokens = 60; // above 50 threshold
    store.Update(conv);

    // Wait for background compaction to complete
    await Task.Delay(500);

    // Verify: GetMessages should return context + last 2 messages (1 pair)
    var messages = store.GetMessages("conv1");

    Assert.Equal("system", messages[0].Role);
    Assert.Contains("Test summary", messages[0].Content);
    Assert.Equal("msg5", messages[1].Id);
    Assert.Equal("msg6", messages[2].Id);
    Assert.Equal(3, messages.Count);

    // Verify summarizer received the right messages
    Assert.Equal(4, summarizer.LastMessages!.Count);
    Assert.Equal("msg1", summarizer.LastMessages[0].Id);
}
```

Add the fake summarizer to the test file (or a separate fakes file):

```csharp
private sealed class FakeCompactionSummarizer(string context) : ICompactionSummarizer
{
    public IReadOnlyList<Message>? LastMessages { get; private set; }
    public string? LastExistingContext { get; private set; }

    public Task<CompactionResult> SummarizeAsync(string? existingContext, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        LastExistingContext = existingContext;
        LastMessages = messages;
        return Task.FromResult(new CompactionResult { Context = context });
    }
}
```

- [ ] **Step 7: Run tests**

Run: `cd src/agent && dotnet test --filter "SqliteConversationStoreTests"`
Expected: All pass

- [ ] **Step 8: Update Program.cs to register CompactionConfig**

In `src/agent/OpenAgent/Program.cs`, add after the existing service registrations:

```csharp
builder.Services.AddSingleton(new CompactionConfig());
```

Add the using: `using OpenAgent.Models.Conversations;`

Note: `ICompactionSummarizer` is intentionally NOT registered yet — compaction stays disabled until we implement the real summarizer.

- [ ] **Step 9: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass

- [ ] **Step 10: Commit**

```
feat: implement conversation compaction with background trigger and rowid cutoff
```

---

## Chunk 3: Expand Tool

### Task 11: Implement the expand tool

The expand tool lets the agent retrieve original messages by ID — used when the summary references messages the agent wants to read in full.

**Files:**
- Create: `src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs`
- Create: `src/agent/OpenAgent.Tools.Expand/OpenAgent.Tools.Expand.csproj`
- Modify: `src/agent/OpenAgent.sln`
- Modify: `src/agent/OpenAgent/Program.cs`
- Test: `src/agent/OpenAgent.Tests/ExpandToolTests.cs`

- [ ] **Step 1: Create the project**

Run:
```bash
cd src/agent && dotnet new classlib -n OpenAgent.Tools.Expand -o OpenAgent.Tools.Expand
cd src/agent && dotnet sln add OpenAgent.Tools.Expand
cd src/agent && dotnet add OpenAgent.Tools.Expand reference OpenAgent.Contracts OpenAgent.Models
```

Remove the auto-generated `Class1.cs`.

- [ ] **Step 2: Update project file for central package management**

Replace the contents of `OpenAgent.Tools.Expand.csproj` — remove any `<Version>` from package references since the project uses Directory.Packages.props.

- [ ] **Step 3: Create ExpandToolHandler**

```csharp
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tools.Expand;

/// <summary>
/// Tool handler that lets the agent retrieve original messages by ID
/// from compacted conversation history.
/// </summary>
public sealed class ExpandToolHandler(IConversationStore store) : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; } = [new ExpandTool(store)];
}

internal sealed class ExpandTool(IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "expand",
        Description = "Retrieve original messages by their IDs from conversation history. Use when the conversation context summary references messages you need to see in full.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                message_ids = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "List of message IDs to retrieve (from [ref: ...] annotations in the context summary)"
                }
            },
            required = new[] { "message_ids" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<ExpandArgs>(arguments);
        if (args?.MessageIds is null or { Count: 0 })
            return Task.FromResult("""{"error": "message_ids is required"}""");

        var messages = store.GetMessagesByIds(args.MessageIds);

        var result = messages.Select(m => new
        {
            id = m.Id,
            role = m.Role,
            content = m.Content,
            created_at = m.CreatedAt.ToString("O"),
            tool_calls = m.ToolCalls,
            tool_call_id = m.ToolCallId
        });

        return Task.FromResult(JsonSerializer.Serialize(result));
    }

    private sealed class ExpandArgs
    {
        [System.Text.Json.Serialization.JsonPropertyName("message_ids")]
        public List<string>? MessageIds { get; set; }
    }
}
```

- [ ] **Step 4: Write test**

Create `src/agent/OpenAgent.Tests/ExpandToolTests.cs`:

```csharp
using OpenAgent.Tests.Fakes;
using OpenAgent.Models.Conversations;
using OpenAgent.Tools.Expand;

namespace OpenAgent.Tests;

public class ExpandToolTests
{
    [Fact]
    public async Task Expand_returns_messages_by_id()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv1", "test", ConversationType.Text);
        store.AddMessage("conv1", new Message { Id = "msg1", ConversationId = "conv1", Role = "user", Content = "hello" });
        store.AddMessage("conv1", new Message { Id = "msg2", ConversationId = "conv1", Role = "assistant", Content = "hi there" });
        store.AddMessage("conv1", new Message { Id = "msg3", ConversationId = "conv1", Role = "user", Content = "bye" });

        var handler = new ExpandToolHandler(store);
        var tool = handler.Tools[0];

        var result = await tool.ExecuteAsync("""{"message_ids": ["msg1", "msg3"]}""");

        Assert.Contains("hello", result);
        Assert.Contains("bye", result);
        Assert.DoesNotContain("hi there", result);
    }

    [Fact]
    public async Task Expand_returns_error_for_empty_ids()
    {
        var store = new InMemoryConversationStore();
        var handler = new ExpandToolHandler(store);
        var tool = handler.Tools[0];

        var result = await tool.ExecuteAsync("""{"message_ids": []}""");

        Assert.Contains("error", result);
    }
}
```

- [ ] **Step 5: Add project reference to test project**

Run: `cd src/agent && dotnet add OpenAgent.Tests reference OpenAgent.Tools.Expand`

- [ ] **Step 6: Run tests**

Run: `cd src/agent && dotnet test --filter "ExpandToolTests"`
Expected: All pass

- [ ] **Step 7: Wire up in Program.cs**

Add to the tool handler registrations:

```csharp
builder.Services.AddSingleton<IToolHandler, ExpandToolHandler>();
```

Add project reference: `cd src/agent && dotnet add OpenAgent reference OpenAgent.Tools.Expand`

Add using: `using OpenAgent.Tools.Expand;`

- [ ] **Step 8: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass

- [ ] **Step 9: Commit**

```
feat: add expand tool for retrieving original messages by ID
```

---

## Chunk 4: Compaction Summarizer Implementation

### Task 12: Implement the compaction summarizer

This calls the LLM with a compaction system prompt to generate the structured summary.

**Files:**
- Create: `src/agent/OpenAgent.Compaction/CompactionSummarizer.cs`
- Create: `src/agent/OpenAgent.Compaction/OpenAgent.Compaction.csproj`
- Modify: `src/agent/OpenAgent.sln`
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Create the project**

Run:
```bash
cd src/agent && dotnet new classlib -n OpenAgent.Compaction -o OpenAgent.Compaction
cd src/agent && dotnet sln add OpenAgent.Compaction
cd src/agent && dotnet add OpenAgent.Compaction reference OpenAgent.Contracts OpenAgent.Models
```

Remove `Class1.cs`. Update the csproj for central package management.

- [ ] **Step 2: Design the compaction system prompt**

Create `src/agent/OpenAgent.Compaction/CompactionPrompt.cs`:

```csharp
namespace OpenAgent.Compaction;

internal static class CompactionPrompt
{
    public const string System = """
        You are a conversation compactor. Your job is to produce a structured summary of conversation messages.

        ## Input

        You receive:
        1. An existing context summary (if any) from a previous compaction cycle
        2. A list of conversation messages to compact

        ## Output Format

        Respond with a JSON object containing:
        - "context": the new structured summary (string)
        - "memories": array of durable facts to remember long-term (strings), or empty array

        ## Context Structure

        The context must be organized by topic with timestamps and message references:

        ```
        ## Topic Name (YYYY-MM-DD HH:mm - HH:mm)
        Key decisions, outcomes, and facts from this topic.
        What was discussed, what was decided, what was the result.
        [ref: msg_id1, msg_id2, msg_id3]
        ```

        ## Rules

        - Group related messages by topic, not chronologically
        - Include timestamps (from message CreatedAt) for each topic section
        - Reference message IDs using [ref: id1, id2, ...] — only reference user and assistant messages, NOT tool result messages
        - For tool calls: summarize what was attempted and the outcome, reference the tool call message ID
        - For tool results: capture the outcome in your summary text, do NOT reference the tool result message ID
        - Roll the existing context summary into the new one — carry forward old [ref: ...] references
        - Prioritize: decisions made, facts established, outcomes of actions
        - Be concise but preserve enough detail that the agent can decide whether to expand references
        - Memories should be durable facts worth persisting long-term (not task-specific details)
        """;
}
```

- [ ] **Step 3: Implement CompactionSummarizer**

Create `src/agent/OpenAgent.Compaction/CompactionSummarizer.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Calls the LLM to generate a structured compaction summary from conversation messages.
/// </summary>
public sealed class CompactionSummarizer : ICompactionSummarizer, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly ILogger<CompactionSummarizer> _logger;

    public CompactionSummarizer(CompactionLlmConfig config, ILogger<CompactionSummarizer> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Endpoint.TrimEnd('/') + "/")
        };
        _httpClient.DefaultRequestHeaders.Add("api-key", config.ApiKey);
        _url = $"openai/deployments/{config.DeploymentName}/chat/completions?api-version={config.ApiVersion}";
    }

    public async Task<CompactionResult> SummarizeAsync(string? existingContext, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        // Build the user message with existing context and messages to compact
        var userContent = new System.Text.StringBuilder();

        if (existingContext is not null)
        {
            userContent.AppendLine("## Existing Context (from previous compaction)");
            userContent.AppendLine(existingContext);
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
        }

        var request = new
        {
            messages = new object[]
            {
                new { role = "system", content = CompactionPrompt.System },
                new { role = "user", content = userContent.ToString() }
            },
            response_format = new { type = "json_object" }
        };

        var response = await _httpClient.PostAsJsonAsync(_url, request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Compaction LLM call failed: {StatusCode} {Body}", (int)response.StatusCode, body);
            throw new HttpRequestException($"Compaction LLM call failed: {(int)response.StatusCode}");
        }

        // Parse the response — extract the assistant message content
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()!;

        // Parse the JSON content
        using var resultDoc = JsonDocument.Parse(content);
        var context = resultDoc.RootElement.GetProperty("context").GetString()!;
        var memories = resultDoc.RootElement.TryGetProperty("memories", out var mem)
            ? mem.EnumerateArray().Select(m => m.GetString()!).ToList()
            : new List<string>();

        _logger.LogInformation("Compaction summary generated: {Length} chars, {MemoryCount} memories", context.Length, memories.Count);

        return new CompactionResult { Context = context, Memories = memories };
    }

    public void Dispose() => _httpClient.Dispose();
}
```

- [ ] **Step 4: Create CompactionLlmConfig**

Create `src/agent/OpenAgent.Compaction/CompactionLlmConfig.cs`:

```csharp
namespace OpenAgent.Compaction;

/// <summary>
/// LLM configuration for the compaction summarizer.
/// Can point to a different deployment/model than the main text provider (e.g. a cheaper model).
/// </summary>
public sealed class CompactionLlmConfig
{
    public required string ApiKey { get; init; }
    public required string Endpoint { get; init; }
    public required string DeploymentName { get; init; }
    public string ApiVersion { get; init; } = "2025-04-01-preview";
}
```

- [ ] **Step 5: Wire up in Program.cs**

Add project reference: `cd src/agent && dotnet add OpenAgent reference OpenAgent.Compaction`

In Program.cs, add configuration for compaction (reads from app settings, only registers if configured):

```csharp
// Compaction summarizer — optional, compaction is disabled without it
var compactionEndpoint = builder.Configuration["Compaction:Endpoint"];
if (!string.IsNullOrEmpty(compactionEndpoint))
{
    var compactionConfig = new CompactionLlmConfig
    {
        ApiKey = builder.Configuration["Compaction:ApiKey"] ?? throw new InvalidOperationException("Compaction:ApiKey is required"),
        Endpoint = compactionEndpoint,
        DeploymentName = builder.Configuration["Compaction:DeploymentName"] ?? throw new InvalidOperationException("Compaction:DeploymentName is required"),
        ApiVersion = builder.Configuration["Compaction:ApiVersion"] ?? "2025-04-01-preview"
    };
    builder.Services.AddSingleton(compactionConfig);
    builder.Services.AddSingleton<ICompactionSummarizer, CompactionSummarizer>();
}
```

Add usings: `using OpenAgent.Compaction;`

- [ ] **Step 6: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All pass (summarizer not registered in tests — compaction stays disabled)

- [ ] **Step 7: Commit**

```
feat: implement CompactionSummarizer with LLM-driven structured summarization
```

---

## Chunk 5: Update InMemoryConversationStore for Tests

### Task 13: Update InMemoryConversationStore to support compaction

The fake store needs to support RowId and GetMessages filtering for tests that exercise compaction-aware code paths.

**Files:**
- Modify: `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`

- [ ] **Step 1: Add auto-incrementing RowId and compaction filtering**

```csharp
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<string, Conversation> _conversations = new();
    private readonly Dictionary<string, List<Message>> _messages = new();
    private long _nextRowId = 1;

    // ... existing IConfigurable members unchanged ...

    public void AddMessage(string conversationId, Message message)
    {
        if (!_messages.ContainsKey(conversationId))
            _messages[conversationId] = [];

        // Assign auto-incrementing RowId
        var withRowId = new Message
        {
            RowId = _nextRowId++,
            Id = message.Id,
            ConversationId = message.ConversationId,
            Role = message.Role,
            Content = message.Content,
            CreatedAt = message.CreatedAt,
            ToolCalls = message.ToolCalls,
            ToolCallId = message.ToolCallId,
            ChannelMessageId = message.ChannelMessageId,
            ReplyToChannelMessageId = message.ReplyToChannelMessageId
        };
        _messages[conversationId].Add(withRowId);
    }

    public IReadOnlyList<Message> GetMessages(string conversationId)
    {
        var conversation = Get(conversationId);
        var list = new List<Message>();

        // Prepend context if compaction has occurred
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

        var allMessages = _messages.GetValueOrDefault(conversationId) ?? [];

        // Filter by compaction cutoff
        var messages = conversation?.CompactedUpToRowId is not null
            ? allMessages.Where(m => m.RowId > conversation.CompactedUpToRowId.Value).ToList()
            : allMessages;

        list.AddRange(messages);
        return list.AsReadOnly();
    }

    public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds)
    {
        var idSet = messageIds.ToHashSet();
        return _messages.Values
            .SelectMany(msgs => msgs)
            .Where(m => idSet.Contains(m.Id))
            .ToList();
    }

    // ... rest unchanged ...
}
```

- [ ] **Step 2: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass

- [ ] **Step 3: Commit**

```
feat: update InMemoryConversationStore with RowId and compaction support
```

---

## Out of Scope (Future Tasks)

These are listed in the design but are separate features:

- **Daily memory writes from extracted memories** — the `CompactionResult.Memories` field is captured but not yet written anywhere. Requires defining where/how memories are stored.
- **Compaction system prompt tuning** — the initial prompt is functional but will need iteration based on real-world compaction quality.
- **Configuration via appsettings** — `CompactionConfig` values are hardcoded defaults; could be made configurable via `appsettings.json`.
