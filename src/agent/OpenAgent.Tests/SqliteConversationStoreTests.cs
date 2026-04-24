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
        var env = new AgentEnvironment { DataPath = _dbDir };
        _store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance, new CompactionConfig());
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    [Fact]
    public void GetMessages_populates_RowId()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");

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

    [Fact]
    public void GetMessages_excludes_compacted_messages()
    {
        var conv = _store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");

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

        // GetMessages returns ONLY post-cut messages. The summary lives on
        // Conversation.Context for providers/UI to render separately.
        var messages = _store.GetMessages("conv1");

        Assert.Single(messages);
        Assert.Equal("msg3", messages[0].Id);

        var refreshed = _store.Get("conv1");
        Assert.NotNull(refreshed);
        Assert.Contains("Old conversation about greetings", refreshed.Context);
    }

    [Fact]
    public void GetMessagesByIds_returns_compacted_messages()
    {
        var conv = _store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");

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

    [Fact]
    public void GetMessages_returns_all_when_no_compaction()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");

        _store.AddMessage("conv1", new Message { Id = "msg1", ConversationId = "conv1", Role = "user", Content = "hello" });
        _store.AddMessage("conv1", new Message { Id = "msg2", ConversationId = "conv1", Role = "assistant", Content = "hi" });

        var messages = _store.GetMessages("conv1");

        Assert.Equal(2, messages.Count);
        Assert.Equal("msg1", messages[0].Id);
        Assert.Equal("msg2", messages[1].Id);
    }

    [Fact]
    public async Task Compaction_summarizes_old_messages_and_updates_cutoff()
    {
        var config = new CompactionConfig
        {
            MaxContextTokens = 100,
            CompactionTriggerPercent = 50,
            KeepRecentTokens = 5 // tiny budget so only the last user turn stays uncompacted
        };
        var summarizer = new FakeCompactionSummarizer("## Summary\nTest summary.\n[ref: msg1, msg2, msg3, msg4]");
        var env = new AgentEnvironment { DataPath = _dbDir };
        using var store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance, config, summarizer);

        var conv = store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");

        for (var i = 1; i <= 6; i++)
        {
            store.AddMessage("conv1", new Message
            {
                Id = $"msg{i}", ConversationId = "conv1",
                Role = i % 2 == 1 ? "user" : "assistant",
                Content = $"message {i}"
            });
        }

        conv.LastPromptTokens = 60;
        store.Update(conv);

        // Wait for background compaction
        await Task.Delay(500);

        var messages = store.GetMessages("conv1");

        Assert.Equal(2, messages.Count);
        Assert.Equal("msg5", messages[0].Id);
        Assert.Equal("msg6", messages[1].Id);

        var freshConv = store.Get("conv1");
        Assert.NotNull(freshConv);
        Assert.Contains("Test summary", freshConv.Context);

        Assert.Equal(4, summarizer.LastMessages!.Count);
        Assert.Equal("msg1", summarizer.LastMessages[0].Id);
    }

    [Fact]
    public void AddMessage_persists_and_reads_back_Modality()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");

        _store.AddMessage("conv1", new Message
        {
            Id = "msg-text", ConversationId = "conv1", Role = "user",
            Content = "typed", Modality = MessageModality.Text
        });
        _store.AddMessage("conv1", new Message
        {
            Id = "msg-voice", ConversationId = "conv1", Role = "user",
            Content = "spoken", Modality = MessageModality.Voice
        });

        var messages = _store.GetMessages("conv1");

        Assert.Equal(2, messages.Count);
        Assert.Equal(MessageModality.Text, messages[0].Modality);
        Assert.Equal(MessageModality.Voice, messages[1].Modality);
    }

    [Fact]
    public void UpdateType_changes_existing_conversation_type()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");

        _store.UpdateType("conv1", ConversationType.Voice);

        var conv = _store.Get("conv1");
        Assert.NotNull(conv);
        Assert.Equal(ConversationType.Voice, conv.Type);
    }

    [Fact]
    public void UpdateType_is_noop_when_type_already_matches()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test-provider", "test-model");

        // Calling with the same type should not throw and should not change anything
        _store.UpdateType("conv1", ConversationType.Text);

        var conv = _store.Get("conv1");
        Assert.NotNull(conv);
        Assert.Equal(ConversationType.Text, conv.Type);
    }

    [Fact]
    public void UpdateType_does_nothing_when_conversation_does_not_exist()
    {
        // Should not throw even if the conversation does not exist
        _store.UpdateType("does-not-exist", ConversationType.Voice);

        Assert.Null(_store.Get("does-not-exist"));
    }

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

        // Without blobs — ToolResultRef is set, FullToolResult is null
        var plain = _store.GetMessages("conv3").Single(m => m.Id == "tm1");
        Assert.Equal("tool-results/tm1.txt", plain.ToolResultRef);
        Assert.Null(plain.FullToolResult);

        // With blobs — FullToolResult populated from disk
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

    [Fact]
    public void AddMessage_persists_full_tool_result_to_disk_and_sets_ref()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "p", "m");

        var toolMsg = new Message
        {
            Id = "tm1",
            ConversationId = "conv1",
            Role = "tool",
            Content = """{"tool":"read","status":"ok","size":21}""",
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

    [Fact]
    public async Task Compaction_uses_per_conversation_ContextWindowTokens_for_threshold()
    {
        // Fallback is huge so the ONLY way this triggers is via per-conversation window.
        var config = new CompactionConfig
        {
            MaxContextTokens = 1_000_000,
            CompactionTriggerPercent = 50,
            KeepRecentTokens = 5
        };
        var summarizer = new FakeCompactionSummarizer("summary");
        var env = new AgentEnvironment { DataPath = _dbDir };
        using var store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance, config, summarizer);

        var conv = store.GetOrCreate("conv1", "test", ConversationType.Text, "p", "m");
        conv.ContextWindowTokens = 100; // tiny per-conv window → trigger = 50
        store.Update(conv);

        for (var i = 1; i <= 6; i++)
        {
            store.AddMessage("conv1", new Message
            {
                Id = $"msg{i}", ConversationId = "conv1",
                Role = i % 2 == 1 ? "user" : "assistant",
                Content = $"message {i}"
            });
        }

        // 60 > 50 (per-conv trigger) but < 500_000 (fallback trigger). If the store used the
        // fallback, compaction would NOT fire.
        var fresh = store.Get("conv1")!;
        fresh.LastPromptTokens = 60;
        store.Update(fresh);

        await Task.Delay(500);

        Assert.NotNull(summarizer.LastMessages);
    }

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

    [Fact]
    public async Task Delete_cancels_in_flight_compaction()
    {
        var gate = new TaskCompletionSource();
        var summarizer = new GatedSummarizer(gate.Task);
        var env = new AgentEnvironment { DataPath = _dbDir };
        var config = new CompactionConfig { KeepRecentTokens = 1, MaxContextTokens = 100, CompactionTriggerPercent = 50 };
        using var store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance, config, summarizer);

        store.GetOrCreate("conv-cancel", "test", ConversationType.Text, "p", "m");
        for (var i = 0; i < 4; i++)
        {
            store.AddMessage("conv-cancel", new Message
            {
                Id = $"m{i}",
                ConversationId = "conv-cancel",
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"message {i}"
            });
        }

        var compactTask = store.CompactNowAsync("conv-cancel", CompactionReason.Manual);

        // Give the background task a moment to reach SummarizeAsync and register its CTS.
        await Task.Delay(50);

        // Delete should cancel the linked token while the summarizer is still blocked.
        store.Delete("conv-cancel");

        // Release the summarizer so it can observe the cancellation.
        gate.SetResult();

        // The compaction task resolves either false (cancelled cleanly) or throws
        // OperationCanceledException — both satisfy the cancellation contract.
        try
        {
            var compacted = await compactTask;
            Assert.False(compacted, "Compaction should not have committed after Delete");
        }
        catch (OperationCanceledException)
        {
            // Also acceptable — the cancellation propagated as an exception.
        }
    }

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

    private sealed class GatedSummarizer(Task gate) : ICompactionSummarizer
    {
        public async Task<CompactionResult> SummarizeAsync(
            string? existingContext,
            IReadOnlyList<Message> messages,
            string? customInstructions = null,
            CancellationToken ct = default)
        {
            // Wait for the test to release, or for cancellation to fire.
            await gate.WaitAsync(ct);
            return new CompactionResult { Context = "done" };
        }
    }

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
}
