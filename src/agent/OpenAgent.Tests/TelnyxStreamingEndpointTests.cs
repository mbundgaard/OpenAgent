using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenAgent;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxStreamingEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public TelnyxStreamingEndpointTests(WebApplicationFactory<Program> f) => _factory = f;

    [Fact]
    public async Task UnknownCallControlId_ClosesImmediately()
    {
        var server = _factory.Server;
        var ws = await server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/webhook/telnyx/abcdef123456/stream?call=unknown"),
            default);
        var buf = new ArraySegment<byte>(new byte[1]);
        var result = await ws.ReceiveAsync(buf, default);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
    }
}
