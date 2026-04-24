# Context Management Rewrite — PR 1: Stop Destroying Tool Results

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve full tool result content across turns by writing tool outputs to disk and referencing them from SQLite. Next-turn LLM requests see the full result the agent actually retrieved, not a summary stub. Unblocks memory continuity (`search_memory` / `load_memory_chunks` results survive), and lays the groundwork for PR 2 (compaction summarizer seeing real content).

**Architecture:** Tool results live on disk at `{dataPath}/conversations/{conversationId}/tool-results/{messageId}.txt`. SQLite stores a relative reference in a new `Messages.ToolResultRef` column. The existing `Content` column keeps the compact summary so UI and old readers remain unaffected. `Message` gets a transient `FullToolResult` property for flowing full content in and out without changing persisted shape.

**Tech Stack:** .NET 10, SQLite, xUnit.

**Spec:** [`2026-04-24-context-management-rewrite.md`](2026-04-24-context-management-rewrite.md)

---

## Chunk 1: Model and Schema

### Task 1: Add `ToolResultRef` and `FullToolResult` to `Message`

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Message.cs`

- [ ] **Step 1: Add `ToolResultRef` persisted property**

Add after the `RowId` property:

```csharp
/// <summary>
/// Relative path to the full tool result on disk, scoped under
/// {dataPath}/conversations/{conversationId}/. Null for non-tool messages and for rows
/// written before this migration. When null, the summary in Content is the only record.
/// </summary>
[JsonPropertyName("tool_result_ref")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? ToolResultRef { get; init; }
```

- [ ] **Step 2: Add `FullToolResult` transient property**

Add below `ToolResultRef`:

```csharp
/// <summary>
/// Full tool result content. Not persisted directly — when set on a new message, the store
/// saves it to disk and populates ToolResultRef. When loaded via
/// IConversationStore.GetMessages(..., includeToolResultBlobs: true), the store reads the
/// blob and populates this field. Null otherwise.
/// </summary>
[JsonIgnore]
public string? FullToolResult { get; init; }
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Success.

- [ ] **Step 4: Commit**

```
feat: add ToolResultRef and FullToolResult to Message model
```

---

### Task 2: Add `ToolResultRef` column to SQLite schema

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Add `TryAddColumn` call for the new column**

In `InitializeDatabase`, after the existing `TryAddColumn` calls for `Messages`, add:

```csharp
TryAddColumn(connection, "Messages", "ToolResultRef", "TEXT");
```

- [ ] **Step 2: Update `SELECT` queries to include `ToolResultRef`**

Find all `SELECT ... FROM Messages` statements. There are three places:

1. `ReadMessagesFromDb` — both the `afterRowId` and no-cutoff variants.
2. `GetMessagesByIds` — the parameterized IN clause query.

Change the column list from:

```csharp
"SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality FROM Messages ..."
```

to:

```csharp
"SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality, ToolResultRef FROM Messages ..."
```

`ToolResultRef` goes at column index 14.

- [ ] **Step 3: Update `ReadMessage` to populate `ToolResultRef`**

Find the `ReadMessage` helper (or equivalent inline reader). Add at the end of the property initializers:

```csharp
ToolResultRef = reader.IsDBNull(14) ? null : reader.GetString(14)
```

Leave `FullToolResult` unset — it's populated only by the explicit blob-loading path (Task 7).

- [ ] **Step 4: Update `AddMessage` INSERT to include `ToolResultRef`**

Find the `AddMessage` INSERT statement. Add `ToolResultRef` to both the column list and the VALUES:

```csharp
cmd.CommandText = """
    INSERT INTO Messages (Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId,
                          ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens,
                          ElapsedMs, Modality, ToolResultRef)
    VALUES (@id, @conversationId, @role, @content, @createdAt, @toolCalls, @toolCallId,
            @channelMessageId, @replyToChannelMessageId, @promptTokens, @completionTokens,
            @elapsedMs, @modality, @toolResultRef)
    """;
```

Add the parameter (initial value `DBNull.Value` — Task 5 populates it from the blob save):

```csharp
cmd.Parameters.AddWithValue("@toolResultRef", DBNull.Value);
```

- [ ] **Step 5: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 6: Commit**

```
feat: add Messages.ToolResultRef column with migration
```

---

## Chunk 2: Blob storage in the SQLite store

### Task 3: Add private blob helpers to `SqliteConversationStore`

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Add a private helper that resolves the blob directory**

Add these private methods near the bottom of the class:

```csharp
/// <summary>
/// Returns the absolute path to the tool-results directory for a conversation.
/// Creates the directory if it does not exist.
/// </summary>
private string GetToolResultsDir(string conversationId)
{
    var dir = Path.Combine(_environment.DataPath, "conversations", conversationId, "tool-results");
    if (!Directory.Exists(dir))
        Directory.CreateDirectory(dir);
    return dir;
}

/// <summary>
/// Writes a tool result to disk atomically and returns the relative path
/// (e.g. "tool-results/abc123.txt") for storage in ToolResultRef.
/// Uses UTF-8 no BOM, matching FileSystemToolHandler.
/// </summary>
private string SaveToolResultBlob(string conversationId, string messageId, string content)
{
    var dir = GetToolResultsDir(conversationId);
    var finalPath = Path.Combine(dir, $"{messageId}.txt");
    var tempPath = $"{finalPath}.tmp";

    File.WriteAllText(tempPath, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    File.Move(tempPath, finalPath, overwrite: true);

    return Path.Combine("tool-results", $"{messageId}.txt").Replace('\\', '/');
}

/// <summary>
/// Reads a tool result from disk. Returns null if the file is missing (logged as a warning) —
/// callers fall back to the compact summary in Message.Content.
/// </summary>
private string? ReadToolResultBlob(string conversationId, string relativePath)
{
    var absolutePath = Path.Combine(_environment.DataPath, "conversations", conversationId, relativePath);
    if (!File.Exists(absolutePath))
    {
        _logger.LogWarning("Tool result blob missing: {Path}", absolutePath);
        return null;
    }

    try
    {
        return File.ReadAllText(absolutePath);
    }
    catch (IOException ex)
    {
        _logger.LogWarning(ex, "Failed to read tool result blob: {Path}", absolutePath);
        return null;
    }
}

/// <summary>
/// Removes the entire conversations/{conversationId} directory, including all tool result blobs.
/// Idempotent.
/// </summary>
private void DeleteConversationBlobs(string conversationId)
{
    var dir = Path.Combine(_environment.DataPath, "conversations", conversationId);
    if (!Directory.Exists(dir)) return;

    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException ex)
    {
        _logger.LogWarning(ex, "Failed to delete conversation blob directory: {Path}", dir);
    }
}
```

Add usings if not present: `using System.Text;`.

- [ ] **Step 2: Build**

Run: `cd src/agent && dotnet build`
Expected: Success (helpers unused so far).

- [ ] **Step 3: Commit**

```
feat: add private blob storage helpers to SqliteConversationStore
```

---

### Task 4: Write `FullToolResult` to disk on `AddMessage`

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Write failing test — AddMessage persists full tool result to disk**

Add to `SqliteConversationStoreTests.cs`:

```csharp
[Fact]
public void AddMessage_persists_full_tool_result_to_disk_and_sets_ref()
{
    _store.GetOrCreate("conv1", "test", ConversationType.Text, "p", "m");

    var toolMsg = new Message
    {
        Id = "tm1",
        ConversationId = "conv1",
        Role = "tool",
        Content = """{"tool":"read","status":"ok","size":12}""",
        FullToolResult = "Hello, world!\nLine 2\n",
        ToolCallId = "call_abc"
    };
    _store.AddMessage("conv1", toolMsg);

    // Verify disk file exists at the expected location
    var expectedPath = Path.Combine(_dbDir, "conversations", "conv1", "tool-results", "tm1.txt");
    Assert.True(File.Exists(expectedPath), $"Expected blob file at {expectedPath}");
    Assert.Equal("Hello, world!\nLine 2\n", File.ReadAllText(expectedPath));

    // Verify ToolResultRef round-trips via default GetMessages (no blob load)
    var messages = _store.GetMessages("conv1");
    var stored = messages.Single(m => m.Id == "tm1");
    Assert.Equal("tool-results/tm1.txt", stored.ToolResultRef);
    Assert.Null(stored.FullToolResult); // not loaded unless explicitly requested
}
```

Run: `cd src/agent && dotnet test --filter "AddMessage_persists_full_tool_result_to_disk_and_sets_ref"`
Expected: FAIL (AddMessage doesn't write the blob yet).

- [ ] **Step 2: Wire `AddMessage` to save the blob**

In `AddMessage`, before the INSERT execute, compute the ref:

```csharp
string? toolResultRef = null;
if (!string.IsNullOrEmpty(message.FullToolResult))
{
    toolResultRef = SaveToolResultBlob(conversationId, message.Id, message.FullToolResult);
}
```

Then change the `@toolResultRef` parameter from `DBNull.Value` to:

```csharp
cmd.Parameters.AddWithValue("@toolResultRef", (object?)toolResultRef ?? DBNull.Value);
```

- [ ] **Step 3: Run test — expect pass**

Run: `cd src/agent && dotnet test --filter "AddMessage_persists_full_tool_result_to_disk_and_sets_ref"`
Expected: PASS.

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat: persist FullToolResult to disk on AddMessage
```

---

### Task 5: Delete blob directory on `Delete(conversationId)`

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Write failing test**

Add to `SqliteConversationStoreTests.cs`:

```csharp
[Fact]
public void Delete_removes_conversation_blob_directory()
{
    _store.GetOrCreate("conv2", "test", ConversationType.Text, "p", "m");
    _store.AddMessage("conv2", new Message
    {
        Id = "tm1", ConversationId = "conv2", Role = "tool",
        Content = "summary", FullToolResult = "full", ToolCallId = "c"
    });

    var blobDir = Path.Combine(_dbDir, "conversations", "conv2");
    Assert.True(Directory.Exists(blobDir));

    var deleted = _store.Delete("conv2");

    Assert.True(deleted);
    Assert.False(Directory.Exists(blobDir), "Blob directory should be removed");
}
```

Run: `cd src/agent && dotnet test --filter "Delete_removes_conversation_blob_directory"`
Expected: FAIL.

- [ ] **Step 2: Call `DeleteConversationBlobs` from `Delete`**

In `Delete(string conversationId)`, after the SQL deletes complete successfully and before the method returns `true`, add:

```csharp
DeleteConversationBlobs(conversationId);
```

- [ ] **Step 3: Run test — expect pass**

Run: `cd src/agent && dotnet test --filter "Delete_removes_conversation_blob_directory"`
Expected: PASS.

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat: remove tool result blobs when a conversation is deleted
```

---

## Chunk 3: Loading blobs on read

### Task 6: Add `includeToolResultBlobs` parameter to `IConversationStore.GetMessages`

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/IConversationStore.cs`
- Modify: `src/agent/OpenAgent.Contracts/IAgentLogic.cs`
- Modify: `src/agent/OpenAgent/AgentLogic.cs`

- [ ] **Step 1: Update `IConversationStore.GetMessages` signature**

In `IConversationStore.cs`:

```csharp
/// <summary>
/// Returns all messages for the given conversation, in order.
/// When <paramref name="includeToolResultBlobs"/> is true, tool result messages have their
/// FullToolResult field populated by reading the on-disk blob referenced by ToolResultRef.
/// If the blob file is missing, FullToolResult stays null and the caller falls back to Content.
/// </summary>
IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false);
```

- [ ] **Step 2: Update `IAgentLogic.GetMessages` signature to match**

In `IAgentLogic.cs`:

```csharp
/// <summary>Returns the full message history for a conversation.</summary>
IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false);
```

- [ ] **Step 3: Update `AgentLogic.GetMessages` pass-through**

In `AgentLogic.cs`:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
    => store.GetMessages(conversationId, includeToolResultBlobs);
```

- [ ] **Step 4: Build**

Run: `cd src/agent && dotnet build`
Expected: Success — existing callers use the default (false) and compile unchanged.

- [ ] **Step 5: Commit**

```
feat: add includeToolResultBlobs parameter to GetMessages
```

---

### Task 7: Implement blob loading in `SqliteConversationStore.GetMessages`

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- Test: `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`

- [ ] **Step 1: Write failing test**

Add to `SqliteConversationStoreTests.cs`:

```csharp
[Fact]
public void GetMessages_with_blobs_populates_FullToolResult()
{
    _store.GetOrCreate("conv3", "test", ConversationType.Text, "p", "m");
    _store.AddMessage("conv3", new Message
    {
        Id = "tm1", ConversationId = "conv3", Role = "tool",
        Content = "summary",
        FullToolResult = "This is the full tool output.\nLine 2.",
        ToolCallId = "c"
    });

    // Without blobs
    var plain = _store.GetMessages("conv3").Single(m => m.Id == "tm1");
    Assert.Equal("tool-results/tm1.txt", plain.ToolResultRef);
    Assert.Null(plain.FullToolResult);

    // With blobs
    var withBlobs = _store.GetMessages("conv3", includeToolResultBlobs: true).Single(m => m.Id == "tm1");
    Assert.Equal("This is the full tool output.\nLine 2.", withBlobs.FullToolResult);
}

[Fact]
public void GetMessages_with_blobs_tolerates_missing_file()
{
    _store.GetOrCreate("conv4", "test", ConversationType.Text, "p", "m");
    _store.AddMessage("conv4", new Message
    {
        Id = "tm1", ConversationId = "conv4", Role = "tool",
        Content = "summary",
        FullToolResult = "full content",
        ToolCallId = "c"
    });

    // Simulate a missing blob file
    var blobPath = Path.Combine(_dbDir, "conversations", "conv4", "tool-results", "tm1.txt");
    File.Delete(blobPath);

    var msg = _store.GetMessages("conv4", includeToolResultBlobs: true).Single(m => m.Id == "tm1");
    Assert.Equal("tool-results/tm1.txt", msg.ToolResultRef);
    Assert.Null(msg.FullToolResult); // missing blob does not throw; falls back to Content
}
```

Run: `cd src/agent && dotnet test --filter "GetMessages_with_blobs"`
Expected: FAIL — the new parameter is ignored.

- [ ] **Step 2: Update `GetMessages` to honor the new parameter**

Change the signature:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
```

After building the `list` (context message prepended + messages from `ReadMessagesFromDb`), if `includeToolResultBlobs` is true, walk the list and populate `FullToolResult`:

```csharp
if (includeToolResultBlobs)
{
    for (var i = 0; i < list.Count; i++)
    {
        var msg = list[i];
        if (msg.Role != "tool" || msg.ToolResultRef is null) continue;

        var full = ReadToolResultBlob(conversationId, msg.ToolResultRef);
        if (full is null) continue; // missing file — fall back to Content downstream

        // Rebuild the message with FullToolResult set (Message is init-only)
        list[i] = new Message
        {
            Id = msg.Id,
            ConversationId = msg.ConversationId,
            Role = msg.Role,
            Content = msg.Content,
            CreatedAt = msg.CreatedAt,
            Modality = msg.Modality,
            ToolCalls = msg.ToolCalls,
            ToolCallId = msg.ToolCallId,
            ChannelMessageId = msg.ChannelMessageId,
            ReplyToChannelMessageId = msg.ReplyToChannelMessageId,
            RowId = msg.RowId,
            PromptTokens = msg.PromptTokens,
            CompletionTokens = msg.CompletionTokens,
            ElapsedMs = msg.ElapsedMs,
            ToolResultRef = msg.ToolResultRef,
            FullToolResult = full
        };
    }
}
```

`list` needs to be a `List<Message>` (not `IReadOnlyList`) locally so it's mutable. Keep the existing body as-is, only add the new block after it.

- [ ] **Step 3: Update `GetMessagesByIds` the same way**

If a caller asks for specific messages by ID, they may also need the blobs. Add the same parameter and behavior:

```csharp
public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds, bool includeToolResultBlobs = false)
```

Update `IConversationStore.GetMessagesByIds` signature too.

Note: this signature change is small but touches the existing `expand` tool. The default value (`false`) keeps existing behavior. The `expand` tool can opt in later if useful — not in this PR.

- [ ] **Step 4: Run tests — expect pass**

Run: `cd src/agent && dotnet test --filter "GetMessages_with_blobs"`
Expected: PASS.

- [ ] **Step 5: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 6: Commit**

```
feat: load FullToolResult from disk when includeToolResultBlobs is requested
```

---

## Chunk 4: Provider integration

### Task 8: Azure OpenAI provider — persist full tool result and read it back

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- Test: `src/agent/OpenAgent.Tests/` (new or existing provider test)

- [ ] **Step 1: Write the `FullToolResult` when persisting tool results**

In the tool-call loop body, find the block that persists tool results:

```csharp
agentLogic.AddMessage(conversationId, new Message
{
    Id = Guid.NewGuid().ToString(),
    ConversationId = conversationId,
    Role = "tool",
    Content = ToolResultSummary.Create(name, result),
    ToolCallId = id,
    Modality = MessageModality.Text
});
```

Add `FullToolResult = result,`:

```csharp
agentLogic.AddMessage(conversationId, new Message
{
    Id = Guid.NewGuid().ToString(),
    ConversationId = conversationId,
    Role = "tool",
    Content = ToolResultSummary.Create(name, result),
    FullToolResult = result,
    ToolCallId = id,
    Modality = MessageModality.Text
});
```

- [ ] **Step 2: Load blobs in `BuildChatMessages`**

Change the `agentLogic.GetMessages(conversation.Id)` call to opt into blob loading:

```csharp
var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);
```

Then in the loop that emits `tool` ChatMessages, prefer `FullToolResult` when set:

```csharp
// Add the matching tool result messages
foreach (var id in expectedIds)
{
    i++;
    var toolMsg = storedMessages[i];
    chatMessages.Add(new ChatMessage
    {
        Role = "tool",
        Content = toolMsg.FullToolResult ?? toolMsg.Content,
        ToolCallId = toolMsg.ToolCallId
    });
}
```

Also the other tool-result branch (single `tool` message with `ToolCallId` but not part of an assistant's tool-call round):

```csharp
if (msg.ToolCallId is not null)
    chatMsg.ToolCallId = msg.ToolCallId;
// Prefer the full tool result when available
if (msg.Role == "tool" && msg.FullToolResult is not null)
    chatMsg.Content = msg.FullToolResult;
chatMessages.Add(chatMsg);
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Success.

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat(azure-openai): persist and reload full tool results across turns
```

---

### Task 9: Anthropic provider — same changes

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`

- [ ] **Step 1: Write `FullToolResult` when persisting tool results**

Find the equivalent tool-result persistence block (look for `Role = "tool"` with `ToolCallId`). Add `FullToolResult = result,` (or whatever the provider calls the raw tool output variable).

- [ ] **Step 2: Load blobs when building messages**

Find the `GetMessages` call in the Anthropic `BuildMessages` helper. Change it to:

```csharp
var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);
```

When mapping stored messages to Anthropic's tool_result content blocks, prefer `FullToolResult`:

```csharp
var toolResultText = storedMessage.FullToolResult ?? storedMessage.Content;
// ... build the Anthropic tool_result block with toolResultText ...
```

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Success.

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```
feat(anthropic): persist and reload full tool results across turns
```

---

## Chunk 5: Test infrastructure

### Task 10: Update `InMemoryConversationStore` to mirror blob behavior

**Files:**
- Modify: `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`

- [ ] **Step 1: Add in-memory blob storage**

Add a private dictionary keyed by `(conversationId, messageId)`:

```csharp
private readonly Dictionary<(string ConversationId, string MessageId), string> _toolResultBlobs = new();
```

- [ ] **Step 2: Update `AddMessage` to capture `FullToolResult`**

If `message.FullToolResult` is set, store it in the dictionary and set `ToolResultRef` on the stored copy:

```csharp
public void AddMessage(string conversationId, Message message)
{
    if (!_messages.ContainsKey(conversationId))
        _messages[conversationId] = [];

    string? toolResultRef = null;
    if (!string.IsNullOrEmpty(message.FullToolResult))
    {
        _toolResultBlobs[(conversationId, message.Id)] = message.FullToolResult;
        toolResultRef = $"tool-results/{message.Id}.txt";
    }

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
        ReplyToChannelMessageId = message.ReplyToChannelMessageId,
        ToolResultRef = toolResultRef
        // FullToolResult intentionally NOT copied — loaded on demand
    };
    _messages[conversationId].Add(withRowId);
}
```

- [ ] **Step 3: Update `GetMessages` to honor `includeToolResultBlobs`**

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
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

    var allMessages = _messages.GetValueOrDefault(conversationId) ?? [];
    var messages = conversation?.CompactedUpToRowId is not null
        ? allMessages.Where(m => m.RowId > conversation.CompactedUpToRowId.Value).ToList()
        : allMessages.ToList();

    if (includeToolResultBlobs)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role != "tool" || msg.ToolResultRef is null) continue;
            if (!_toolResultBlobs.TryGetValue((conversationId, msg.Id), out var full)) continue;

            messages[i] = msg with { FullToolResult = full };
        }
    }

    list.AddRange(messages);
    return list.AsReadOnly();
}
```

Note: `Message with { ... }` requires `Message` to be a `record` or support `with`. If `Message` is `sealed class` without `record` support, build the copy explicitly as in Task 7.

- [ ] **Step 4: Update `GetMessagesByIds` signature to match**

```csharp
public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds, bool includeToolResultBlobs = false)
{
    var idSet = messageIds.ToHashSet();
    var results = _messages.Values
        .SelectMany(msgs => msgs)
        .Where(m => idSet.Contains(m.Id))
        .ToList();

    if (includeToolResultBlobs)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var msg = results[i];
            if (msg.Role != "tool" || msg.ToolResultRef is null) continue;
            if (!_toolResultBlobs.TryGetValue((msg.ConversationId, msg.Id), out var full)) continue;
            results[i] = new Message { /* explicit copy with FullToolResult = full */ };
        }
    }

    return results;
}
```

- [ ] **Step 5: Update `Delete` to drop blob entries**

```csharp
public bool Delete(string conversationId)
{
    var removed = _conversations.Remove(conversationId);
    _messages.Remove(conversationId);

    // Remove blob entries for this conversation
    var keysToRemove = _toolResultBlobs.Keys.Where(k => k.ConversationId == conversationId).ToList();
    foreach (var key in keysToRemove)
        _toolResultBlobs.Remove(key);

    return removed;
}
```

- [ ] **Step 6: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 7: Commit**

```
feat(tests): mirror tool-result blob behavior in InMemoryConversationStore
```

---

### Task 11: End-to-end test — tool results survive across turns

**Files:**
- Create or modify: `src/agent/OpenAgent.Tests/ToolResultContinuityTests.cs`

- [ ] **Step 1: Write the continuity test**

This exercises the core PR goal: across two provider calls in the same conversation, the second call sees the full tool result in its built ChatMessages.

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class ToolResultContinuityTests
{
    [Fact]
    public void Full_tool_result_survives_across_turns_via_store()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv1", "test", ConversationType.Text, "p", "m");

        // Simulate turn 1: a tool result is persisted with full content
        var fullResult = "Line 1\nLine 2\nLine 3\n(this is the full tool output)";
        store.AddMessage("conv1", new Message
        {
            Id = "tm1", ConversationId = "conv1", Role = "tool",
            Content = """{"tool":"read","status":"ok","size":48}""",
            FullToolResult = fullResult,
            ToolCallId = "call_1"
        });

        // Turn 2: a different caller reads messages with blob loading
        var messages = store.GetMessages("conv1", includeToolResultBlobs: true);
        var toolMsg = messages.Single(m => m.Id == "tm1");

        Assert.Equal(fullResult, toolMsg.FullToolResult);

        // And without blob loading, the summary is the visible content
        var uiMessages = store.GetMessages("conv1");
        var uiToolMsg = uiMessages.Single(m => m.Id == "tm1");
        Assert.Null(uiToolMsg.FullToolResult);
        Assert.Equal("""{"tool":"read","status":"ok","size":48}""", uiToolMsg.Content);
    }
}
```

- [ ] **Step 2: Run the test**

Run: `cd src/agent && dotnet test --filter "Full_tool_result_survives_across_turns_via_store"`
Expected: PASS.

- [ ] **Step 3: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 4: Commit**

```
test: tool results survive across turns end-to-end
```

---

## Verification

After all tasks are complete:

- [ ] **Step 1: Final full test run**

Run: `cd src/agent && dotnet test`
Expected: All pass.

- [ ] **Step 2: Manual smoke check**

Run the agent locally against a simple prompt that exercises a tool (e.g. `file_read`). After the response, inspect:

1. `{dataPath}/conversations/{conversationId}/tool-results/` contains a `.txt` file for the tool call.
2. The contents of the file match what the tool returned.
3. The `Messages` row for that tool result has both `Content` (summary) and `ToolResultRef` populated.
4. Send a follow-up prompt that should reference the prior tool output. Verify the agent responds correctly, implying the LLM received the full content.

- [ ] **Step 3: Delete the test conversation and verify the blob directory is removed**

`DELETE /api/conversations/{conversationId}` → confirm `{dataPath}/conversations/{conversationId}/` no longer exists.

---

## Out of Scope (Covered by Later PRs)

- **PR 2** — Cut point algorithm, user-role summary message, per-model context window, `keepRecentTokens`.
- **PR 3** — Overflow and manual triggers, iterative summary prompts, cancellation, structured compaction logs.

Also explicitly not in this PR:

- Bulk-rewrite of old rows. Pre-migration rows keep `ToolResultRef = NULL` and continue to show only the summary in `Content`. Providers fall back on both reads and writes to old behavior for those.
- Orphan blob sweep. Missing-but-referenced and referenced-but-missing are both log-and-continue; no background cleanup yet.
- Blob size capping or age-based offloading. Deferred until measured.
- `expand` tool uplift to load full content. The existing tool keeps returning summaries; if it needs the full result, pass `includeToolResultBlobs: true` in a later change.
