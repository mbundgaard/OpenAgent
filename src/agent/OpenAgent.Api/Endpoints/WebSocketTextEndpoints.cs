using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Text;

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
            IConversationStore store, AgentConfig agentConfig, IServiceProvider services) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            // Create conversation if needed, then resolve provider per-message inside the loop
            store.GetOrCreate(conversationId, "app",
                agentConfig.TextProvider, agentConfig.TextModel,
                agentConfig.VoiceProvider, agentConfig.VoiceModel);

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            var registry = services.GetRequiredService<IWebSocketRegistry>();
            registry.Register(conversationId, ws);

            try
            {
                await RunChatLoopAsync(ws, conversationId, store, agentConfig, services, context.RequestAborted);
            }
            finally
            {
                registry.Unregister(conversationId, ws);

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
        }).RequireAuthorization();
    }

    private static async Task RunChatLoopAsync(
        WebSocket ws, string conversationId, IConversationStore store,
        AgentConfig agentConfig, IServiceProvider services, CancellationToken ct)
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
            var request = JsonSerializer.Deserialize<TextWebSocketInboundMessage>(json, JsonOptions);

            if (request?.Content is null)
                continue;

            // Re-read conversation and resolve provider per message — picks up provider/model changes
            var conversation = store.GetOrCreate(conversationId, "app",
                agentConfig.TextProvider, agentConfig.TextModel,
                agentConfig.VoiceProvider, agentConfig.VoiceModel);
            var textProvider = services.GetRequiredKeyedService<ILlmTextProvider>(conversation.TextProvider);

            var userMessage = new Message
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Role = "user",
                Content = request.Content,
                Modality = MessageModality.Text
            };

            await foreach (var evt in textProvider.CompleteAsync(conversation, userMessage, ct))
            {
                switch (evt)
                {
                    case TextDelta delta:
                        await SendJsonAsync(ws, new TextWebSocketDelta { Content = delta.Content }, ct);
                        break;
                    case ToolCallEvent toolCall:
                        await SendJsonAsync(ws, new TextWebSocketToolCall
                        {
                            ToolCallId = toolCall.ToolCallId,
                            Name = toolCall.Name,
                            Arguments = toolCall.Arguments
                        }, ct);
                        break;
                    case ToolResultEvent toolResult:
                        await SendJsonAsync(ws, new TextWebSocketToolResult
                        {
                            ToolCallId = toolResult.ToolCallId,
                            Name = toolResult.Name,
                            Result = toolResult.Result
                        }, ct);
                        break;
                    case ToolCallStarted:
                        await SendJsonAsync(ws, new TextWebSocketToolCallStarted(), ct);
                        break;
                    case ToolCallCompleted:
                        await SendJsonAsync(ws, new TextWebSocketToolCallCompleted(), ct);
                        break;
                }
            }

            await SendJsonAsync(ws, new TextWebSocketDone(), ct);
        }
    }

    private static async Task SendJsonAsync<T>(WebSocket ws, T value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
    }

}
