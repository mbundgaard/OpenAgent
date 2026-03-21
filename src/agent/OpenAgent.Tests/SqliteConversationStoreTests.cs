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

        // Now GetMessages should only return msg3 plus the context
        var messages = _store.GetMessages("conv1");

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Contains("Old conversation about greetings", messages[0].Content);
        Assert.Equal("msg3", messages[1].Id);
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
            KeepLatestMessagePairs = 1
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

        Assert.Equal("system", messages[0].Role);
        Assert.Contains("Test summary", messages[0].Content);
        Assert.Equal("msg5", messages[1].Id);
        Assert.Equal("msg6", messages[2].Id);
        Assert.Equal(3, messages.Count);

        Assert.Equal(4, summarizer.LastMessages!.Count);
        Assert.Equal("msg1", summarizer.LastMessages[0].Id);
    }

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
}
