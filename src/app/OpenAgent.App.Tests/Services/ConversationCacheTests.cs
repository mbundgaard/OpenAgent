using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Services;

public class ConversationCacheTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ccache_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    [Fact]
    public async Task Round_trips_list()
    {
        var c = new ConversationCache(_tmp);
        var items = new List<ConversationListItem>
        {
            new() { Id = "a", Source = "app", CreatedAt = DateTimeOffset.UtcNow }
        };
        await c.WriteAsync(items);
        var got = await c.ReadAsync();
        Assert.Single(got!);
        Assert.Equal("a", got![0].Id);
    }

    [Fact]
    public async Task Read_when_missing_returns_null()
    {
        var c = new ConversationCache(_tmp);
        Assert.Null(await c.ReadAsync());
    }

    [Fact]
    public async Task Read_corrupted_returns_null_and_does_not_throw()
    {
        Directory.CreateDirectory(_tmp);
        await File.WriteAllTextAsync(Path.Combine(_tmp, "conversations.cache.json"), "{not json");
        var c = new ConversationCache(_tmp);
        Assert.Null(await c.ReadAsync());
    }
}
