using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Anonymous webhook endpoint for pushing events into existing conversations.
/// The conversation GUID in the URL is the capability — unguessable by design.
/// No auto-create (404 if missing), no sync wait (202 returned immediately).
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>
    /// Maps POST /api/webhook/conversation/{conversationId} for pushing a plain-text
    /// body as a user message into an existing conversation.
    /// </summary>
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhook/conversation/{conversationId}", async (
            string conversationId,
            HttpRequest request,
            IConversationStore store,
            IServiceProvider services,
            DeliveryRouter deliveryRouter,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            // Read raw body as UTF-8 text — Content-Type is not validated
            using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var body = await reader.ReadToEndAsync(ct);

            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "body is empty" });

            var conversation = store.Get(conversationId);
            if (conversation is null)
                return Results.NotFound();

            // Webhooks are gated by the unguessable conversation-GUID in the URL —
            // they're trusted system events (file-mover, scheduled tasks, etc.), not
            // human chat traffic. The conversation's mention filter does not apply.

            var userMessage = new Message
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Role = "user",
                Content = body,
                Modality = MessageModality.Text
            };

            var textProvider = services.GetRequiredKeyedService<ILlmTextProvider>(conversation.TextProvider);
            var logger = loggerFactory.CreateLogger("WebhookEndpoints");

            // Fire-and-forget — intentionally use CancellationToken.None so the completion
            // survives the HTTP response being sent. Safe because IConversationStore and
            // the provider are singletons, not request-scoped.
            //
            // We accumulate TextDelta events into the assistant's reply and route it through
            // DeliveryRouter so a webhook-driven completion in a channel-bound conversation
            // (e.g. file-mover library notifications on a WhatsApp group) actually surfaces in
            // the chat. Without this delivery hop the reply only lands in conversation history.
            _ = Task.Run(async () =>
            {
                var sb = new StringBuilder();
                try
                {
                    await foreach (var evt in textProvider.CompleteAsync(conversation, userMessage, CancellationToken.None))
                    {
                        if (evt is TextDelta delta)
                            sb.Append(delta.Content);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Webhook completion failed for conversation {ConversationId}", conversationId);
                    return;
                }

                var response = sb.ToString();

                // [] sentinel — agent signalled "nothing worth sending" (silent background flow).
                if (response.Trim() == "[]")
                {
                    logger.LogInformation("Webhook reply suppressed by [] sentinel for conversation {ConversationId}", conversationId);
                    return;
                }

                if (string.IsNullOrWhiteSpace(response))
                {
                    logger.LogDebug("Webhook completion produced no text for conversation {ConversationId}; nothing to deliver", conversationId);
                    return;
                }

                try
                {
                    // Re-fetch in case channel binding shifted during completion.
                    var current = store.Get(conversationId) ?? conversation;
                    await deliveryRouter.DeliverAsync(current, response, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Webhook delivery failed for conversation {ConversationId}", conversationId);
                }
            });

            return Results.Accepted();
        }).AllowAnonymous();
    }
}
