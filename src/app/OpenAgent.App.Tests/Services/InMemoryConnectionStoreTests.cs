using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Services;

public class InMemoryConnectionStoreTests
{
    private static ServerConnection Conn(string id = "c1", string name = "Test", string url = "https://h/", string token = "tok")
        => new(id, name, url, token);

    [Fact]
    public async Task LoadAll_empty_returns_empty_list()
    {
        var store = new InMemoryConnectionStore();
        var all = await store.LoadAllAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task Save_and_LoadAll_round_trips()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn());
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("c1", all[0].Id);
        Assert.Equal("Test", all[0].Name);
    }

    [Fact]
    public async Task Save_updates_existing_by_id()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn());
        await store.SaveAsync(Conn(name: "Renamed"));
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("Renamed", all[0].Name);
    }

    [Fact]
    public async Task Delete_removes_and_returns_remaining_count()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("a"));
        await store.SaveAsync(Conn("b"));
        var remaining = await store.DeleteAsync("a");
        Assert.Equal(1, remaining);
        var all = await store.LoadAllAsync();
        Assert.Single(all);
        Assert.Equal("b", all[0].Id);
    }

    [Fact]
    public async Task Delete_nonexistent_returns_current_count()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn());
        var remaining = await store.DeleteAsync("nonexistent");
        Assert.Equal(1, remaining);
    }

    [Fact]
    public async Task SetActive_and_GetActiveId_round_trips()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("a"));
        await store.SaveAsync(Conn("b"));
        await store.SetActiveAsync("b");
        Assert.Equal("b", await store.GetActiveIdAsync());
    }

    [Fact]
    public async Task LoadActive_returns_active_connection()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("a", url: "https://a/"));
        await store.SaveAsync(Conn("b", url: "https://b/"));
        await store.SetActiveAsync("b");
        var active = await store.LoadActiveAsync();
        Assert.Equal("https://b/", active!.BaseUrl);
    }

    [Fact]
    public async Task LoadActive_returns_null_when_empty()
    {
        var store = new InMemoryConnectionStore();
        Assert.Null(await store.LoadActiveAsync());
    }

    [Fact]
    public async Task LoadActive_falls_back_to_first_when_active_id_missing()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("a"));
        await store.SetActiveAsync("deleted");
        var active = await store.LoadActiveAsync();
        Assert.Equal("a", active!.Id);
    }

    [Fact]
    public async Task First_save_auto_sets_active()
    {
        var store = new InMemoryConnectionStore();
        await store.SaveAsync(Conn("first"));
        Assert.Equal("first", await store.GetActiveIdAsync());
    }
}
