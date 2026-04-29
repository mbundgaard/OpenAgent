using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Services;

public class InMemoryCredentialStoreTests
{
    [Fact]
    public async Task Round_trip_returns_what_was_saved()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveAsync(new QrPayload("https://h/", "tok"));
        var got = await store.LoadAsync();
        Assert.Equal("https://h/", got!.BaseUrl);
        Assert.Equal("tok", got.Token);
    }

    [Fact]
    public async Task Load_on_empty_returns_null()
    {
        var store = new InMemoryCredentialStore();
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Clear_removes_credentials()
    {
        var store = new InMemoryCredentialStore();
        await store.SaveAsync(new QrPayload("https://h/", "tok"));
        await store.ClearAsync();
        Assert.Null(await store.LoadAsync());
    }
}
