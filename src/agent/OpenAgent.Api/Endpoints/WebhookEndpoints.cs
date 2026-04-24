using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;

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
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            // Read raw body as UTF-8 text — Content-Type is not validated
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);

            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "body is empty" });

            return Results.Accepted();
        }).AllowAnonymous();
    }
}
