# Conversation Mention Filter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-conversation `MentionFilter` list. When set, inbound user messages whose text does not contain any listed name (case-insensitive substring) are silently dropped before any side effect across Telegram, WhatsApp, REST chat, and webhook entry points.

**Architecture:** Nullable `List<string>?` column on `Conversation`, mirroring the existing `ActiveSkills` storage pattern. A single pure helper `MentionMatcher.ShouldAccept` is called at each user-message entry point before typing indicators or LLM calls. The API exposes the field via the existing `PATCH /api/conversations/{id}` update route and both response DTOs.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Microsoft.Data.Sqlite, xUnit + WebApplicationFactory. Source tree rooted at `src/agent/`.

---

## Spec Reference

Full design at `docs/superpowers/specs/2026-04-25-conversation-mention-filter-design.md`. Read it first. Key behaviors:

- `MentionFilter` null or empty → always reply (current behavior).
- `MentionFilter` non-empty → drop inbound user text that matches none of the names (case-insensitive `string.Contains`).
- Drop = no DB write, no typing indicator, no LLM call. One `LogDebug` line.
- PATCH semantics: field omitted/null → unchanged, `[]` → clear, non-empty → replace.

## File Map

**New files**
- `src/agent/OpenAgent.Models/Conversations/MentionMatcher.cs` — static helper class.
- `src/agent/OpenAgent.Tests/MentionMatcherTests.cs` — unit tests for the helper.

**Modified — models & store**
- `src/agent/OpenAgent.Models/Conversations/Conversation.cs` — add `MentionFilter` property.
- `src/agent/OpenAgent.Models/Conversations/ConversationResponses.cs` — add field on both response DTOs and on `UpdateConversationRequest`.
- `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs` — migration, SELECT columns, INSERT/UPDATE params, `ReadConversation` hydration.

**Modified — entry points**
- `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs` — gate user text after text-type filter, before `SendTypingAsync`.
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs` — gate after dedup, before `SendComposingAsync`.
- `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs` — gate before provider resolution.
- `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs` — gate before spawning the background completion.
- `src/agent/OpenAgent.Api/Endpoints/ConversationEndpoints.cs` — wire `MentionFilter` through PATCH + GET DTOs.

**Modified — tests & fakes**
- `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs` — no structural change required (it already round-trips the whole `Conversation` via `Update`), but verify.
- `src/agent/OpenAgent.Tests/ConversationEndpointTests.cs` — PATCH round-trip.
- `src/agent/OpenAgent.Tests/ChatEndpointTests.cs` — drop-when-unmatched behavior.
- `src/agent/OpenAgent.Tests/WebhookEndpointTests.cs` — drop-when-unmatched behavior.
- `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs` — drop-when-unmatched behavior.
- `src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs` — drop-when-unmatched behavior.

## Conventions

- No emojis in code/comments. XML doc comments on new public types and their public methods.
- Variables are explicit: `conversationId`, `userText`, not bare `id` / `text`.
- `[JsonPropertyName("snake_case")]` on serialized fields. `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` to suppress absent optional fields.
- Match existing file style — the projects are idiomatic C# with top-level namespaces, file-scoped namespaces, and sparse comments.

---

### Task 1: Add `MentionFilter` property to the `Conversation` model

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Conversation.cs`

- [ ] **Step 1: Add the property**

Open `src/agent/OpenAgent.Models/Conversations/Conversation.cs` and append the property inside the `Conversation` class, right after the `Intention` property at the end of the file:

```csharp
    /// <summary>
    /// Case-insensitive trigger names for incoming user messages. When non-empty,
    /// inbound user text that does not contain any of these names (substring match)
    /// is silently dropped before persistence or LLM invocation.
    /// Null or empty means "reply to all" — the default behavior.
    /// </summary>
    [JsonPropertyName("mention_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MentionFilter { get; set; }
```

- [ ] **Step 2: Build to verify**

Run:
```
cd src/agent && dotnet build OpenAgent.Models/OpenAgent.Models.csproj
```
Expected: build succeeds.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.Models/Conversations/Conversation.cs
git commit -m "feat(models): add MentionFilter to Conversation"
```

---

### Task 2: Add the `MentionFilter` helper with unit tests

Write the tests first.

**Files:**
- Create: `src/agent/OpenAgent.Models/Conversations/MentionMatcher.cs`
- Create: `src/agent/OpenAgent.Tests/MentionMatcherTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/MentionMatcherTests.cs`:

```csharp
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class MentionMatcherTests
{
    private static Conversation Conv(List<string>? mentionNames) => new()
    {
        Id = "c1",
        Source = "test",
        Type = ConversationType.Text,
        Provider = "p",
        Model = "m",
        MentionFilter = mentionNames
    };

    [Fact]
    public void ShouldAccept_NullMentionFilter_AcceptsAnyText()
    {
        Assert.True(MentionMatcher.ShouldAccept(Conv(null), "anything"));
        Assert.True(MentionMatcher.ShouldAccept(Conv(null), ""));
    }

    [Fact]
    public void ShouldAccept_EmptyMentionFilter_AcceptsAnyText()
    {
        Assert.True(MentionMatcher.ShouldAccept(Conv([]), "anything"));
    }

    [Fact]
    public void ShouldAccept_TextContainsName_CaseInsensitive_Accepts()
    {
        var conv = Conv(["Dex"]);
        Assert.True(MentionMatcher.ShouldAccept(conv, "hey Dex"));
        Assert.True(MentionMatcher.ShouldAccept(conv, "hey DEX!"));
        Assert.True(MentionMatcher.ShouldAccept(conv, "hey dex"));
    }

    [Fact]
    public void ShouldAccept_TextMissingName_Rejects()
    {
        var conv = Conv(["Dex"]);
        Assert.False(MentionMatcher.ShouldAccept(conv, "hello world"));
        Assert.False(MentionMatcher.ShouldAccept(conv, ""));
    }

    [Fact]
    public void ShouldAccept_SubstringMatch_MatchesInsideOtherWords()
    {
        // Documented v1 semantics: substring match, not word-boundary.
        var conv = Conv(["Dex"]);
        Assert.True(MentionMatcher.ShouldAccept(conv, "look at the index"));
    }

    [Fact]
    public void ShouldAccept_MultipleNames_AnyMatchAccepts()
    {
        var conv = Conv(["Dex", "fox"]);
        Assert.True(MentionMatcher.ShouldAccept(conv, "hello fox"));
        Assert.True(MentionMatcher.ShouldAccept(conv, "hello DEX"));
        Assert.False(MentionMatcher.ShouldAccept(conv, "hello cat"));
    }

    [Fact]
    public void ShouldAccept_EmptyOrWhitespaceNames_AreIgnored()
    {
        // A lone empty string must not match everything.
        var conv = Conv(["", "Dex"]);
        Assert.False(MentionMatcher.ShouldAccept(conv, "hello"));
        Assert.True(MentionMatcher.ShouldAccept(conv, "dex"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~MentionFilterTests
```
Expected: compile error — `MentionFilter` not found.

- [ ] **Step 3: Write the helper**

Create `src/agent/OpenAgent.Models/Conversations/MentionMatcher.cs`:

```csharp
namespace OpenAgent.Models.Conversations;

/// <summary>
/// Decides whether an incoming user message should be processed based on
/// the conversation's <see cref="Conversation.MentionFilter"/> list.
/// </summary>
public static class MentionMatcher
{
    /// <summary>
    /// Returns true when the message should be processed. A conversation with
    /// no mention names (null or empty) accepts any text. Otherwise the text
    /// must contain at least one non-empty name as a case-insensitive substring.
    /// </summary>
    public static bool ShouldAccept(Conversation conversation, string userText)
    {
        if (conversation.MentionFilter is null || conversation.MentionFilter.Count == 0)
            return true;

        foreach (var name in conversation.MentionFilter)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            if (userText.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~MentionFilterTests
```
Expected: all 7 tests pass.

- [ ] **Step 5: Commit**

```
git add src/agent/OpenAgent.Models/Conversations/MentionMatcher.cs src/agent/OpenAgent.Tests/MentionMatcherTests.cs
git commit -m "feat(models): add MentionFilter helper for mention-gated conversations"
```

---

### Task 3: Persist `MentionFilter` in `SqliteConversationStore`

Add the migration, extend every SELECT column list, include the column in INSERT and UPDATE, and hydrate it in `ReadConversation`. Mirror the existing `ActiveSkills` code.

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Add the migration line**

Open `SqliteConversationStore.cs`. Find the block of `TryAddColumn` calls starting around line 89. Add this line at the end of the block (right after `TryAddColumn(connection, "Conversations", "ContextWindowTokens", "INTEGER");`):

```csharp
        TryAddColumn(connection, "Conversations", "MentionFilter", "TEXT");
```

- [ ] **Step 2: (no CREATE TABLE change needed)**

The file's convention is that `CREATE TABLE IF NOT EXISTS Conversations` holds only a minimal schema (Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens) — every other column is introduced via `TryAddColumn`. Keep that convention. The single `TryAddColumn` from Step 1 covers both fresh install and upgrade.

- [ ] **Step 3: Add MentionFilter to SELECT column lists**

There are three SELECT statements that list columns from `Conversations`, each currently ending with `ContextWindowTokens FROM Conversations ...`. Find each and append `, MentionFilter` to the column list before `FROM`. The three locations:

1. `FindChannelConversation` (around line 169).
2. `GetAll` (around line 238).
3. `Get` (around line 252).

Before (example for `Get`):
```csharp
cmd.CommandText = "SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning, Provider, Model, TotalPromptTokens, TotalCompletionTokens, TurnCount, LastActivity, ActiveSkills, ChannelType, ConnectionId, ChannelChatId, DisplayName, Intention, ContextWindowTokens FROM Conversations WHERE Id = @id";
```

After:
```csharp
cmd.CommandText = "SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning, Provider, Model, TotalPromptTokens, TotalCompletionTokens, TurnCount, LastActivity, ActiveSkills, ChannelType, ConnectionId, ChannelChatId, DisplayName, Intention, ContextWindowTokens, MentionFilter FROM Conversations WHERE Id = @id";
```

Apply the same change (append `, MentionFilter` before `FROM`) to the other two SELECTs.

- [ ] **Step 4: Hydrate `MentionFilter` in `ReadConversation`**

Find `ReadConversation` (around line 753). The last field currently read is `ContextWindowTokens = reader.IsDBNull(22) ? null : reader.GetInt32(22)`. Add a new line after it:

```csharp
            ContextWindowTokens = reader.IsDBNull(22) ? null : reader.GetInt32(22),
            MentionFilter = reader.IsDBNull(23) ? null : JsonSerializer.Deserialize<List<string>>(reader.GetString(23))
```

Make sure the previous `ContextWindowTokens` line now ends with a comma.

- [ ] **Step 5: Include `MentionFilter` in the two INSERT statements**

There are two places with `INSERT ... INTO Conversations (...)`. Both currently list the same columns. For each:

Before:
```csharp
cmd.CommandText = """
    INSERT OR IGNORE INTO Conversations (Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, Provider, Model, ActiveSkills, ChannelType, ConnectionId, ChannelChatId)
    VALUES (@id, @source, @type, @createdAt, @voiceSessionId, @voiceSessionOpen, @provider, @model, @activeSkills, @channelType, @connectionId, @channelChatId)
    """;
```

After (append `, MentionFilter` to the column list and `, @mentionNames` to the values list):
```csharp
cmd.CommandText = """
    INSERT OR IGNORE INTO Conversations (Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, Provider, Model, ActiveSkills, ChannelType, ConnectionId, ChannelChatId, MentionFilter)
    VALUES (@id, @source, @type, @createdAt, @voiceSessionId, @voiceSessionOpen, @provider, @model, @activeSkills, @channelType, @connectionId, @channelChatId, @mentionNames)
    """;
```

Then add the parameter binding right after the existing `@channelChatId` parameter:

```csharp
cmd.Parameters.AddWithValue("@mentionNames",
    conversation.MentionFilter is { Count: > 0 }
        ? (object)JsonSerializer.Serialize(conversation.MentionFilter)
        : DBNull.Value);
```

Apply the same SQL change and parameter addition to the second INSERT (in `FindOrCreateChannelConversation`). For that second INSERT, `conversation.MentionFilter` is not set on newly-created channel conversations, so the binding is effectively `DBNull.Value` — write it in the same form for consistency.

- [ ] **Step 6: Include `MentionFilter` in `Update`**

In `Update` (around line 259), extend the UPDATE SET list. Current tail:

```csharp
cmd.CommandText = """
    UPDATE Conversations
    SET Source = @source, Type = @type, VoiceSessionId = @voiceSessionId,
        VoiceSessionOpen = @voiceSessionOpen, LastPromptTokens = @lastPromptTokens,
        Context = @context, CompactedUpToRowId = @compactedUpToRowId,
        CompactionRunning = @compactionRunning, Provider = @provider, Model = @model,
        TotalPromptTokens = @totalPromptTokens, TotalCompletionTokens = @totalCompletionTokens,
        TurnCount = @turnCount, LastActivity = @lastActivity, ActiveSkills = @activeSkills,
        ChannelType = @channelType, ConnectionId = @connectionId, ChannelChatId = @channelChatId,
        Intention = @intention, ContextWindowTokens = @contextWindowTokens
    WHERE Id = @id
    """;
```

Add `, MentionFilter = @mentionNames` to the SET list (after `ContextWindowTokens = @contextWindowTokens`, before the newline):

```csharp
        Intention = @intention, ContextWindowTokens = @contextWindowTokens,
        MentionFilter = @mentionNames
```

Then add the parameter after `@contextWindowTokens` (around line 298):

```csharp
cmd.Parameters.AddWithValue("@mentionNames",
    conversation.MentionFilter is { Count: > 0 }
        ? (object)JsonSerializer.Serialize(conversation.MentionFilter)
        : DBNull.Value);
```

- [ ] **Step 7: Build to verify**

Run:
```
cd src/agent && dotnet build OpenAgent.ConversationStore.Sqlite/OpenAgent.ConversationStore.Sqlite.csproj
```
Expected: build succeeds.

- [ ] **Step 8: Add a round-trip test**

Open `src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs`. The fixture already exposes a shared `_store` field wired up via its constructor. Add this test at the end of the class, before the `Dispose()` method:

```csharp
    [Fact]
    public void MentionFilter_RoundTripsThroughUpdateAndGet()
    {
        var conversationId = Guid.NewGuid().ToString();
        var conv = _store.GetOrCreate(conversationId, "app", ConversationType.Text, "p", "m");

        conv.MentionFilter = ["Dex", "fox"];
        _store.Update(conv);

        var reloaded = _store.Get(conversationId);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.MentionFilter);
        Assert.Equal(new[] { "Dex", "fox" }, reloaded.MentionFilter);

        // Clearing via empty list stores NULL and hydrates as null.
        reloaded.MentionFilter = [];
        _store.Update(reloaded);

        var cleared = _store.Get(conversationId);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.MentionFilter);
    }
```

- [ ] **Step 9: Run the new test**

Run:
```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~SqliteConversationStoreTests.MentionFilter_RoundTrips
```
Expected: pass.

- [ ] **Step 10: Commit**

```
git add src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs src/agent/OpenAgent.Tests/SqliteConversationStoreTests.cs
git commit -m "feat(store): persist MentionFilter on Conversations"
```

---

### Task 4: Expose `MentionFilter` on response DTOs and PATCH request

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/ConversationResponses.cs`

- [ ] **Step 1: Add the field to `ConversationListItemResponse`**

Open `src/agent/OpenAgent.Models/Conversations/ConversationResponses.cs`. Inside `ConversationListItemResponse`, after the existing `Intention` property, add:

```csharp
    [JsonPropertyName("mention_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MentionFilter { get; init; }
```

- [ ] **Step 2: Add the field to `ConversationDetailResponse`**

In the same file, inside `ConversationDetailResponse`, after its `Intention` property, add:

```csharp
    [JsonPropertyName("mention_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? MentionFilter { get; init; }
```

- [ ] **Step 3: Add the field to `UpdateConversationRequest`**

In the same file, inside `UpdateConversationRequest`, after the existing `Intention` property, add:

```csharp
    [JsonPropertyName("mention_filter")]
    public List<string>? MentionFilter { get; init; }
```

- [ ] **Step 4: Build to verify**

```
cd src/agent && dotnet build OpenAgent.Models/OpenAgent.Models.csproj
```
Expected: build succeeds.

- [ ] **Step 5: Commit**

```
git add src/agent/OpenAgent.Models/Conversations/ConversationResponses.cs
git commit -m "feat(models): expose MentionFilter on conversation DTOs"
```

---

### Task 5: Wire `MentionFilter` through `ConversationEndpoints` PATCH and GETs

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/ConversationEndpoints.cs`
- Modify: `src/agent/OpenAgent.Tests/ConversationEndpointTests.cs`

- [ ] **Step 1: Write the failing API-level test**

Open `src/agent/OpenAgent.Tests/ConversationEndpointTests.cs`. Add this test after `ListConversations_OrdersByLastActivity`:

```csharp
    [Fact]
    public async Task PatchConversation_MentionFilter_RoundTrips()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversation = store.GetOrCreate(Guid.NewGuid().ToString(), "app", ConversationType.Text, "test-provider", "test-model");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");

        // Set a list
        var setResponse = await client.PatchAsJsonAsync(
            $"/api/conversations/{conversation.Id}",
            new { mention_filter = new[] { "Dex", "fox" } });
        setResponse.EnsureSuccessStatusCode();

        var afterSet = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation.Id}");
        var names = afterSet.GetProperty("mention_filter").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "Dex", "fox" }, names);

        // Clear with empty list → absent in response
        var clearResponse = await client.PatchAsJsonAsync(
            $"/api/conversations/{conversation.Id}",
            new { mention_filter = Array.Empty<string>() });
        clearResponse.EnsureSuccessStatusCode();

        var afterClear = await client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversation.Id}");
        Assert.False(afterClear.TryGetProperty("mention_filter", out _),
            "mention_filter should be omitted when null (JsonIgnoreWhenWritingNull)");
    }
```

Add a using for `System.Text.Json` at the top of the file if it isn't already present.

- [ ] **Step 2: Run the test to verify it fails**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~ConversationEndpointTests.PatchConversation_MentionFilter
```
Expected: FAIL — the PATCH doesn't handle `mention_filter` yet, so the GET still shows it null/absent after the "set" call. Exact failure: `Assert.Equal` on the names array.

- [ ] **Step 3: Handle `MentionFilter` in PATCH**

Open `src/agent/OpenAgent.Api/Endpoints/ConversationEndpoints.cs`. Locate the PATCH handler around line 93. Find the `Intention` block (around line 108) and add a `MentionFilter` block right after it:

```csharp
            // Empty string explicitly clears the intention; null leaves it unchanged.
            if (request.Intention is not null)
                conversation.Intention = request.Intention.Length == 0 ? null : request.Intention;

            // Empty list explicitly clears MentionFilter; null leaves it unchanged.
            if (request.MentionFilter is not null)
                conversation.MentionFilter = request.MentionFilter.Count == 0 ? null : request.MentionFilter;
```

- [ ] **Step 4: Include `MentionFilter` on the PATCH response DTO**

In the same PATCH handler, the response is built via `new ConversationDetailResponse { ... }`. Add `MentionFilter = conversation.MentionFilter,` to the initializer list — put it right after `Intention = conversation.Intention`.

- [ ] **Step 5: Include `MentionFilter` on the GET-detail response**

Find the `GET /{conversationId}` handler (around line 50). Its `new ConversationDetailResponse { ... }` also needs `MentionFilter = conversation.MentionFilter,` in the same position.

- [ ] **Step 6: Include `MentionFilter` on the list response**

Find the `GET /` (list) handler (around line 29). Each element is built via `new ConversationListItemResponse { ... }`. Add `MentionFilter = c.MentionFilter,` in the same position (after `Intention`).

- [ ] **Step 7: Run the test to verify it passes**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~ConversationEndpointTests.PatchConversation_MentionFilter
```
Expected: PASS.

- [ ] **Step 8: Run the full test suite to make sure nothing regressed**

```
cd src/agent && dotnet test
```
Expected: all tests pass.

- [ ] **Step 9: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/ConversationEndpoints.cs src/agent/OpenAgent.Tests/ConversationEndpointTests.cs
git commit -m "feat(api): surface MentionFilter on conversation read/patch"
```

---

### Task 6: Gate REST chat endpoint with `MentionFilter`

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs`
- Modify: `src/agent/OpenAgent.Tests/ChatEndpointTests.cs`

- [ ] **Step 1: Write the failing test**

Open `src/agent/OpenAgent.Tests/ChatEndpointTests.cs`. Add this test (and `using OpenAgent.Contracts;` + `using Microsoft.Extensions.DependencyInjection;` + `using OpenAgent.Models.Conversations;` at the top if any are missing — the existing file already has them):

```csharp
    [Fact]
    public async Task SendMessage_NoMentionMatch_DropsAndReturnsEmptyEvents()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        var conv = store.GetOrCreate(conversationId, "app", ConversationType.Text, "azure-openai-text", "test-model");
        conv.MentionFilter = ["Dex"];
        store.Update(conv);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new { Content = "hello there" });

        response.EnsureSuccessStatusCode();
        var events = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    [Fact]
    public async Task SendMessage_MentionMatch_Responds()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        var conv = store.GetOrCreate(conversationId, "app", ConversationType.Text, "azure-openai-text", "test-model");
        conv.MentionFilter = ["Dex"];
        store.Update(conv);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new { Content = "hey Dex what's up" });

        response.EnsureSuccessStatusCode();
        var events = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(events);
        Assert.NotEmpty(events);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~ChatEndpointTests.SendMessage_NoMentionMatch_DropsAndReturnsEmptyEvents
```
Expected: FAIL — current behavior returns the fake's two text events.

- [ ] **Step 3: Apply the filter**

Open `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs`. After the conversation is loaded/flipped but **before** the `textProvider` resolution (around line 42), add:

```csharp
            if (!MentionMatcher.ShouldAccept(conversation, request.Content ?? string.Empty))
                return Results.Json(Array.Empty<object>(), JsonOptions);
```

Ensure `using OpenAgent.Models.Conversations;` is at the top of the file (it already is).

- [ ] **Step 4: Run the tests to verify they pass**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~ChatEndpointTests
```
Expected: all ChatEndpointTests pass, including the two new ones.

- [ ] **Step 5: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs src/agent/OpenAgent.Tests/ChatEndpointTests.cs
git commit -m "feat(api): gate /messages POST with MentionFilter"
```

---

### Task 7: Gate webhook endpoint with `MentionFilter`

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs`
- Modify: `src/agent/OpenAgent.Tests/WebhookEndpointTests.cs`

- [ ] **Step 1: Write the failing test**

Open `src/agent/OpenAgent.Tests/WebhookEndpointTests.cs`. Add this test:

```csharp
    [Fact]
    public async Task PostWebhook_NoMentionMatch_DropsAndDoesNotTriggerCompletion()
    {
        var store = _factory.Services.GetRequiredService<IConversationStore>();
        var conversationId = Guid.NewGuid().ToString();
        var conv = store.GetOrCreate(conversationId, "app", ConversationType.Text, "azure-openai-text", "test-model");
        conv.MentionFilter = ["Dex"];
        store.Update(conv);

        var callCountBefore = _capturingProvider.CallCount;

        var client = _factory.CreateClient();
        var response = await client.PostAsync(
            $"/api/webhook/conversation/{conversationId}",
            new StringContent("hello there", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Give the background task a chance to (incorrectly) run.
        await Task.Delay(200);
        Assert.Equal(callCountBefore, _capturingProvider.CallCount);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~WebhookEndpointTests.PostWebhook_NoMentionMatch
```
Expected: FAIL — `CallCount` increments by 1.

- [ ] **Step 3: Apply the filter**

Open `src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs`. Right after the `if (conversation is null) return Results.NotFound();` line, add:

```csharp
            if (!MentionMatcher.ShouldAccept(conversation, body))
                return Results.Accepted();
```

Add `using OpenAgent.Models.Conversations;` at the top if it is not already there (it is).

- [ ] **Step 4: Run the test to verify it passes**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~WebhookEndpointTests
```
Expected: all WebhookEndpointTests pass.

- [ ] **Step 5: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/WebhookEndpoints.cs src/agent/OpenAgent.Tests/WebhookEndpointTests.cs
git commit -m "feat(webhook): gate conversation push with MentionFilter"
```

---

### Task 8: Gate Telegram handler with `MentionFilter`

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`
- Modify: `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`

- [ ] **Step 1: Write the failing test**

Open `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`. Add these tests at the bottom of the class:

```csharp
    [Fact]
    public async Task HandleUpdateAsync_MentionFilterSet_NoMatch_DropsSilently()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("should not reach");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();

        // First turn creates the conversation with an LLM reply
        var firstUpdate = CreatePrivateTextUpdate(AllowedUserId, ChatId, "hey Dex");
        var convInit = store.FindOrCreateChannelConversation("telegram", ConnectionId, ChatId.ToString(),
            "telegram", Models.Conversations.ConversationType.Text, "azure-openai-text", "gpt-5.2-chat");
        convInit.MentionFilter = ["Dex"];
        store.Update(convInit);

        // Second turn without the mention must be dropped
        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "hello world");
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        // Dropped before side effects: no typing, no send of any kind.
        Assert.Empty(sender.TypingCalls);
        Assert.Empty(sender.HtmlCalls);
        Assert.Empty(sender.TextCalls);
    }

    [Fact]
    public async Task HandleUpdateAsync_MentionFilterSet_Match_Replies()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Hi back");
        var handler = new TelegramMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, TestAgentConfig, CreateOptions(AllowedUserId));
        var sender = new FakeTelegramSender();

        // Pre-create with MentionFilter=[Dex]
        var conv = store.FindOrCreateChannelConversation("telegram", ConnectionId, ChatId.ToString(),
            "telegram", Models.Conversations.ConversationType.Text, "azure-openai-text", "gpt-5.2-chat");
        conv.MentionFilter = ["Dex"];
        store.Update(conv);

        var update = CreatePrivateTextUpdate(AllowedUserId, ChatId, "hey Dex");
        await handler.HandleUpdateAsync(sender, update, CancellationToken.None);

        Assert.Single(sender.HtmlCalls);
        Assert.Contains("Hi back", sender.HtmlCalls[0].Html);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~TelegramMessageHandlerTests.HandleUpdateAsync_MentionFilterSet_NoMatch
```
Expected: FAIL — typing and reply are sent.

- [ ] **Step 3: Apply the filter**

Open `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`. Find the section right after the conversation is looked up / created (the block that ends with `_store.UpdateDisplayName(...)` around line 128) and before `// Resolve provider from the conversation...` (around line 132). Insert:

```csharp
        // Mention filter — drop silently if the conversation limits inbound messages to
        // those containing one of the configured names.
        if (!OpenAgent.Models.Conversations.MentionMatcher.ShouldAccept(conversation, userText))
        {
            _logger?.LogDebug("Mention filter dropped message in conversation {ConversationId}", conversation.Id);
            return;
        }
```

Why here rather than earlier: we need the `conversation` object loaded first to read `MentionFilter`. Placement is still before `SendTypingAsync` (which happens at line ~107 — wait, check the order).

**Important:** In the current file, `SendTypingAsync` is called **before** `FindOrCreateChannelConversation`. That means typing currently goes out before we even look up the conversation. To honor "no typing on drop", we need to move the typing call to after the mention check. Do that as part of this step:

1. Find the `// Send typing indicator (best-effort, don't fail the whole flow)` block (around line 104). Cut the entire `try { await sender.SendTypingAsync(...); } catch { ... }` block.
2. Paste it *after* the mention filter block you just added, before the `// Resolve provider from the conversation` comment.

Final order inside `HandleUpdateAsync` from that point:
1. Conversation gating (AllowNewConversations) — unchanged.
2. Get-or-create conversation + display name refresh — unchanged.
3. **New:** mention filter (early return).
4. **Moved:** `SendTypingAsync` try/catch.
5. Resolve provider, build user message, call LLM — unchanged.

Double-check after editing: re-open the file and confirm the order matches the five points above.

- [ ] **Step 4: Run the tests to verify they pass**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~TelegramMessageHandlerTests
```
Expected: all TelegramMessageHandlerTests pass, including the two new ones. The pre-existing tests must still pass — they don't set `MentionFilter`, so the filter is a no-op for them.

- [ ] **Step 5: Commit**

```
git add src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs
git commit -m "feat(telegram): gate inbound messages with MentionFilter"
```

---

### Task 9: Gate WhatsApp handler with `MentionFilter`

**Files:**
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs`
- Modify: `src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs`

- [ ] **Step 1: Write the failing test**

Open `src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs`. Add two tests near the other `[Fact]`s (use `AllowedDmChatId` and the existing constants; match the file's constructor style):

```csharp
    [Fact]
    public async Task MentionFilterSet_NoMatch_DropsSilently()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("should not reach");
        var handler = new WhatsAppMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, new AgentConfig { TextProvider = "azure-openai-text", TextModel = "gpt-5.2-chat" });
        var sender = new FakeWhatsAppSender();

        var conv = store.FindOrCreateChannelConversation("whatsapp", ConnectionId, AllowedDmChatId,
            "whatsapp", ConversationType.Text, "azure-openai-text", "gpt-5.2-chat");
        conv.MentionFilter = ["Dex"];
        store.Update(conv);

        var message = CreateTextMessage(AllowedDmChatId, "hello world");
        await handler.HandleMessageAsync(sender, message, CancellationToken.None);

        Assert.Empty(sender.ComposingCalls);
        Assert.Empty(sender.TextCalls);
    }

    [Fact]
    public async Task MentionFilterSet_Match_Replies()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Hi back");
        var handler = new WhatsAppMessageHandler(store, new FakeConnectionStore(ConnectionId), _ => provider, ConnectionId, new AgentConfig { TextProvider = "azure-openai-text", TextModel = "gpt-5.2-chat" });
        var sender = new FakeWhatsAppSender();

        var conv = store.FindOrCreateChannelConversation("whatsapp", ConnectionId, AllowedDmChatId,
            "whatsapp", ConversationType.Text, "azure-openai-text", "gpt-5.2-chat");
        conv.MentionFilter = ["Dex"];
        store.Update(conv);

        var message = CreateTextMessage(AllowedDmChatId, "hey Dex!");
        await handler.HandleMessageAsync(sender, message, CancellationToken.None);

        Assert.Single(sender.ComposingCalls);
        Assert.Contains("Hi back", sender.TextCalls[0].Text);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~WhatsAppMessageHandlerTests.MentionFilterSet_NoMatch
```
Expected: FAIL — composing + text calls happen.

- [ ] **Step 3: Apply the filter**

Open `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs`. The current flow is:

1. Validate required fields.
2. Dedup.
3. Conversation gating (AllowNewConversations).
4. `SendComposingAsync` (line ~94).
5. `FindOrCreateChannelConversation` (line ~107).
6. Build user message and call LLM.

We need the conversation loaded first to read `MentionFilter`. Restructure so the get-or-create happens **before** composing, then add the filter between them:

- Move the `FindOrCreateChannelConversation` call (currently at line ~107, with `conversation` variable and `textProvider` resolution) to just after the "Conversation gating" block (right after `connection.AllowNewConversations = false; _connectionStore.Save(...);` / the end of the first `if (existing is null)` block).
- Add the mention filter immediately after:

```csharp
        // Mention filter — drop silently if the conversation limits inbound messages
        // to those containing one of the configured names.
        if (!OpenAgent.Models.Conversations.MentionMatcher.ShouldAccept(conversation, message.Text))
        {
            _logger?.LogDebug("Mention filter dropped message in conversation {ConversationId}", conversation.Id);
            return;
        }
```

- Keep the existing `SendComposingAsync` try/catch — now it runs only for accepted messages.
- The subsequent `textProvider` resolution and user-message construction use the already-loaded `conversation`.

After editing, the flow inside `HandleMessageAsync` must be exactly:
1. Validate required fields.
2. Dedup.
3. Conversation gating (AllowNewConversations).
4. `FindOrCreateChannelConversation`.
5. **New:** mention filter.
6. `SendComposingAsync` (best-effort).
7. Resolve provider, build user message, call LLM, reply.

Double-check after editing: re-open the file and confirm the order matches the seven points above. Delete the now-duplicated `FindOrCreateChannelConversation` call if two exist after refactoring.

- [ ] **Step 4: Run the tests to verify they pass**

```
cd src/agent && dotnet test OpenAgent.Tests/OpenAgent.Tests.csproj --filter FullyQualifiedName~WhatsAppMessageHandlerTests
```
Expected: all WhatsAppMessageHandlerTests pass. Existing tests must still pass (no `MentionFilter` set → filter is no-op).

- [ ] **Step 5: Commit**

```
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs
git commit -m "feat(whatsapp): gate inbound messages with MentionFilter"
```

---

### Task 10: Full-suite verification and API doc update

**Files:**
- Modify: `CLAUDE.md` (API Reference table entries)

- [ ] **Step 1: Run the entire test suite**

```
cd src/agent && dotnet test
```
Expected: all tests pass, including the pre-existing suite. If anything outside the scope of this change regresses, stop and investigate — the Sqlite store migration is the most likely culprit (column ordering in SELECT must match the `ReadConversation` index assignments).

- [ ] **Step 2: Update the API reference for PATCH**

Open `CLAUDE.md`. Find the Conversations section in "API Reference". The current rows list GET/GET/DELETE/POST for conversations. There is no documented PATCH entry currently. Add one row to the Conversations table (after the DELETE row):

```
| `PATCH` | `/api/conversations/{conversationId}` | Update writable conversation fields (`source`, `provider`, `model`, `channel_chat_id`, `intention`, `mention_filter`). Field omitted → unchanged. Empty string / empty array → clear. |
```

If a PATCH row already exists (the table may have been updated since this plan was written), edit it to include `mention_filter` in the field list instead of adding a duplicate row.

- [ ] **Step 3: Commit doc and final verification**

```
git add CLAUDE.md
git commit -m "docs: document mention_filter on PATCH /api/conversations"
```

Then run the build once more for good measure:

```
cd src/agent && dotnet build
```
Expected: build succeeds with no warnings introduced by this change.

---

## Self-review checklist (complete before handoff)

- [ ] All 10 tasks committed. Each commit builds and its tests pass.
- [ ] `MentionMatcher.ShouldAccept` rule matches the spec: null/empty list → accept; non-empty → case-insensitive substring.
- [ ] Sqlite migration, SELECTs, both INSERTs, UPDATE, and `ReadConversation` all reference `MentionFilter`. Missing any one of them causes a runtime exception or silently drops the value — verify all five touchpoints exist.
- [ ] PATCH semantics match existing `Intention` pattern: field absent → unchanged, empty array → clear, non-empty → replace.
- [ ] Telegram handler order: conversation load → mention filter → typing indicator → LLM. No typing sent on drop.
- [ ] WhatsApp handler order: conversation load → mention filter → composing indicator → LLM. No composing sent on drop.
- [ ] REST chat and webhook both early-return before provider calls on drop; webhook still returns 202 (not an error).
- [ ] `LogDebug` lines present on every drop path (REST chat, webhook, Telegram, WhatsApp) — one concise message per drop.
