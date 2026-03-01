using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Bidirectional text chat over WebSocket — client sends JSON messages, server responds with text completions.
/// Maintains a persistent connection for low-latency conversational exchange.
/// Will support token-by-token streaming when the text provider gains a streaming API.
/// </summary>
public static class WebSocketTextEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Maps /ws/conversations/{conversationId}/text for bidirectional text chat.
    /// </summary>
    public static void MapWebSocketTextEndpoints(this WebApplication app)
    {
        app.Map("/ws/conversations/{conversationId}/text", async (string conversationId, HttpContext context,
            IConversationStore store, ILlmTextProvider textProvider) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            if (store.Get(conversationId) is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();

            try
            {
                await RunChatLoopAsync(ws, conversationId, textProvider, context.RequestAborted);
            }
            finally
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    catch { /* best-effort close */ }
                }
            }
        });
    }

    private static async Task RunChatLoopAsync(
        WebSocket ws, string conversationId, ILlmTextProvider textProvider, CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var request = JsonSerializer.Deserialize<InboundMessage>(json, JsonOptions);

            if (request?.Content is null)
                continue;

            var response = await textProvider.CompleteAsync(conversationId, request.Content, ct);

            await SendJsonAsync(ws, new { type = "message", role = response.Role, content = response.Content }, ct);
        }
    }

    private static async Task SendJsonAsync<T>(WebSocket ws, T value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
    }

    private sealed class InboundMessage
    {
        public string? Content { get; set; }
    }
}
