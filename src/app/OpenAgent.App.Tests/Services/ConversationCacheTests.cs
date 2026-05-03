using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Services;

public class ConversationCacheTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ccache_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    [Fact]
    public async Task Round_trips_list_for_connection()
    {
        var c = new ConversationCache(_tmp);
        var items = new List<ConversationListItem>
        {
            new() { Id = "a", Source = "app", CreatedAt = DateTimeOffset.UtcNow }
        };
        await c.WriteAsync("conn1", items);
        var got = await c.ReadAsync("conn1");
        Assert.Single(got!);
        Assert.Equal("a", got![0].Id);
    }

    [Fact]
    public async Task Read_when_missing_returns_null()
    {
        var c = new ConversationCache(_tmp);
        Assert.Null(await c.ReadAsync("conn1"));
    }

    [Fact]
    public async Task Read_corrupted_returns_null_and_does_not_throw()
    {
        Directory.CreateDirectory(_tmp);
        await File.WriteAllTextAsync(Path.Combine(_tmp, "conversations-conn1.cache.json"), "{not json");
        var c = new ConversationCache(_tmp);
        Assert.Null(await c.ReadAsync("conn1"));
    }

    [Fact]
    public async Task Different_connections_have_separate_caches()
    {
        var c = new ConversationCache(_tmp);
        var items1 = new List<ConversationListItem> { new() { Id = "a", Source = "app", CreatedAt = DateTimeOffset.UtcNow } };
        var items2 = new List<ConversationListItem> { new() { Id = "b", Source = "telegram", CreatedAt = DateTimeOffset.UtcNow } };
        await c.WriteAsync("conn1", items1);
        await c.WriteAsync("conn2", items2);
        var got1 = await c.ReadAsync("conn1");
        var got2 = await c.ReadAsync("conn2");
        Assert.Equal("a", got1![0].Id);
        Assert.Equal("b", got2![0].Id);
    }

    [Fact]
    public async Task DeleteCache_removes_file()
    {
        var c = new ConversationCache(_tmp);
        var items = new List<ConversationListItem> { new() { Id = "a", Source = "app", CreatedAt = DateTimeOffset.UtcNow } };
        await c.WriteAsync("conn1", items);
        c.DeleteCache("conn1");
        Assert.Null(await c.ReadAsync("conn1"));
    }

    [Fact]
    public void DeleteCache_does_not_throw_when_missing()
    {
        var c = new ConversationCache(_tmp);
        c.DeleteCache("nonexistent");
    }
}
