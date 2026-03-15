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
}
