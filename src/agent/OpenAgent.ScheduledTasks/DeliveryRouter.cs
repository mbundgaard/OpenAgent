using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Text;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// The "where to send the result" stage of task execution. Tries two delivery paths:
///   1. Channel-bound (Telegram/WhatsApp) → IOutboundSender
///   2. Active WebSocket → push delta+done events to connected web app client
/// If neither applies, delivery is silent — the response is already in the conversation history.
///
/// Delivery failures don't throw: we log and move on, because the task itself succeeded
/// (the LLM completion ran). The distinction matters for ConsecutiveErrors — we don't want
/// transient delivery flakiness to mark the task run as failed.
/// </summary>
internal sealed class DeliveryRouter(
    IConnectionManager connectionManager,
    IWebSocketRegistry webSocketRegistry,
    ILogger<DeliveryRouter> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Delivers the response based on the conversation's channel binding or active WebSocket.
    /// </summary>
    public async Task DeliverAsync(Conversation conversation, string response, CancellationToken ct)
    {
        // Channel-bound → deliver via outbound sender
        if (conversation.ChannelType is not null && conversation.ConnectionId is not null && conversation.ChannelChatId is not null)
        {
            await DeliverToChannelAsync(conversation, response, ct);
            return;
        }

        // Active WebSocket → push to connected web app client
        var ws = webSocketRegistry.Get(conversation.Id);
        if (ws is not null)
        {
            await DeliverToWebSocketAsync(ws, response, ct);
            return;
        }

        // No delivery target — response is already persisted in conversation history
    }

    private async Task DeliverToChannelAsync(Conversation conversation, string response, CancellationToken ct)
    {
        var provider = connectionManager.GetProvider(conversation.ConnectionId!);
        if (provider is not IOutboundSender sender)
        {
            logger.LogWarning(
                "Conversation {ConversationId}: connection '{ConnectionId}' does not support outbound messaging. Output stays in history only.",
                conversation.Id, conversation.ConnectionId);
            return;
        }

        try
        {
            await sender.SendMessageAsync(conversation.ChannelChatId!, response, ct);
            logger.LogInformation(
                "Delivered response to {ChannelType} {ConnectionId}:{ChatId}",
                conversation.ChannelType, conversation.ConnectionId, conversation.ChannelChatId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to deliver response to {ChannelType} {ConnectionId}:{ChatId}",
                conversation.ChannelType, conversation.ConnectionId, conversation.ChannelChatId);
        }
    }

    private async Task DeliverToWebSocketAsync(WebSocket ws, string response, CancellationToken ct)
    {
        try
        {
            // Send as delta + done — same protocol the text WS endpoint uses
            var deltaJson = JsonSerializer.SerializeToUtf8Bytes(
                new TextWebSocketDelta { Content = response }, JsonOptions);
            await ws.SendAsync(deltaJson, WebSocketMessageType.Text, true, ct);

            var doneJson = JsonSerializer.SerializeToUtf8Bytes(
                new TextWebSocketDone(), JsonOptions);
            await ws.SendAsync(doneJson, WebSocketMessageType.Text, true, ct);

            logger.LogInformation("Delivered response to WebSocket for conversation {ConversationId}",
                "websocket");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deliver response to WebSocket");
        }
    }
}
