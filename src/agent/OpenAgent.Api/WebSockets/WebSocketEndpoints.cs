using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OpenAgent.Api.WebSockets;

public static class WebSocketEndpoints
{
    public static void MapWebSocketEndpoints(this WebApplication app)
    {
        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            await EchoLoop(ws, context.RequestAborted);
        });
    }

    private static async Task EchoLoop(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, null, ct);
                break;
            }

            await ws.SendAsync(
                buffer.AsMemory(0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                ct);
        }
    }
}
