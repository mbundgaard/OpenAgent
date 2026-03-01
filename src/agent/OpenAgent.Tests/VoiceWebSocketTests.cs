using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenAgent.Tests;

public class VoiceWebSocketTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public VoiceWebSocketTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task VoiceEndpoint_NonWebSocket_Returns400()
    {
        var response = await _client.GetAsync("/ws/conversations/test-123/voice");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
