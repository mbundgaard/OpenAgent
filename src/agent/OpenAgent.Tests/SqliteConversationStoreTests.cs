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
}
