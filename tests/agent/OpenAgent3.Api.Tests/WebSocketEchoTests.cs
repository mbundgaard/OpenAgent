using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenAgent3.Api.Tests;

public class WebSocketEchoTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebSocketEchoTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task WebSocket_SendMessage_EchoesBack()
    {
        var client = _factory.Server.CreateWebSocketClient();
        var ws = await client.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, "/ws"), CancellationToken.None);

        var message = new { type = "text_message", conversationId = "test-123", text = "hello" };
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        var echoJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var echo = JsonDocument.Parse(echoJson);
        Assert.Equal("text_message", echo.RootElement.GetProperty("type").GetString());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
}
