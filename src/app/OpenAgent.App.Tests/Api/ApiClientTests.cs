using System.Net;
using OpenAgent.App.Core.Api;
using OpenAgent.App.Core.Onboarding;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Tests.Api;

public class ApiClientTests
{
    private (ApiClient client, StubHandler stub) Make(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var store = new InMemoryCredentialStore();
        store.SaveAsync(new QrPayload("https://agent.example/", "tok123")).GetAwaiter().GetResult();
        var stub = new StubHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var http = new HttpClient(stub);
        return (new ApiClient(http, store), stub);
    }

    [Fact]
    public async Task Get_conversations_sends_api_key_and_parses()
    {
        var (client, stub) = Make(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":"x","source":"app","text_provider":"o","text_model":"m","voice_provider":"o","voice_model":"m","created_at":"2026-04-29T10:00:00Z","total_prompt_tokens":0,"total_completion_tokens":0,"turn_count":0}]""")
            });
        var items = await client.GetConversationsAsync();
        Assert.Single(items);
        Assert.Equal("https://agent.example/api/conversations", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok123", stub.LastRequest.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task Delete_returns_when_204()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await client.DeleteConversationAsync("x");
    }

    [Fact]
    public async Task Patch_intention_sends_json_body()
    {
        string? body = null;
        var (client, _) = Make(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await client.RenameConversationAsync("x", "Hello world");
        Assert.Contains("\"intention\":\"Hello world\"", body);
    }

    [Fact]
    public async Task Throws_AuthRejected_on_401()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<AuthRejectedException>(() => client.GetConversationsAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Rename_throws_on_empty_intention(string? intention)
    {
        var (client, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => client.RenameConversationAsync("x", intention!));
    }

    [Fact]
    public async Task Throws_ApiException_on_5xx_with_status_code()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("internal failure")
        });
        var ex = await Assert.ThrowsAsync<ApiException>(() => client.GetConversationsAsync());
        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task Throws_NetworkException_on_transport_failure()
    {
        var (client, _) = Make(_ => throw new HttpRequestException("boom"));
        var ex = await Assert.ThrowsAsync<NetworkException>(() => client.GetConversationsAsync());
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }
}
