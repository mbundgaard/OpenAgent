using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// The "where to send the result" stage of task execution. Reads delivery intent from the
/// conversation, not the task — channel-bound conversations (ChannelType/ConnectionId/
/// ChannelChatId set) deliver their output to the external chat via IOutboundSender.
/// Unbound conversations are silent: the response is already in the conversation history,
/// nothing extra is sent.
///
/// Delivery failures don't throw: we log and move on, because the task itself succeeded
/// (the LLM completion ran). The distinction matters for ConsecutiveErrors — we don't want
/// transient delivery flakiness to mark the task run as failed.
/// </summary>
internal sealed class DeliveryRouter(
    IConnectionManager connectionManager,
    ILogger<DeliveryRouter> logger)
{
    /// <summary>
    /// Delivers the response based on the conversation's channel binding.
    /// If the conversation has no channel binding, this is a no-op (silent).
    /// </summary>
    public async Task DeliverAsync(Conversation conversation, string response, CancellationToken ct)
    {
        // No channel binding → silent delivery. Response already persisted by the provider.
        if (conversation.ChannelType is null || conversation.ConnectionId is null || conversation.ChannelChatId is null)
            return;

        var provider = connectionManager.GetProvider(conversation.ConnectionId);
        if (provider is not IOutboundSender sender)
        {
            logger.LogWarning(
                "Conversation {ConversationId}: connection '{ConnectionId}' does not support outbound messaging. Output stays in history only.",
                conversation.Id, conversation.ConnectionId);
            return;
        }

        try
        {
            await sender.SendMessageAsync(conversation.ChannelChatId, response, ct);
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
}
