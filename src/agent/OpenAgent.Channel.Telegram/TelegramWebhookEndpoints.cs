using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Maps the Telegram webhook endpoint that receives updates from the Telegram Bot API.
/// Routes by auto-generated webhookId so the user never needs to know connection IDs.
/// </summary>
public static class TelegramWebhookEndpoints
{
    /// <summary>
    /// Maps POST /api/webhook/telegram/{webhookId} — receives updates from Telegram,
    /// validates the secret token, and processes the update asynchronously.
    /// </summary>
    public static void MapTelegramWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhook/telegram/{webhookId}", async (
            string webhookId,
            HttpRequest request,
            IConnectionManager connectionManager,
            ILogger<TelegramChannelProvider> logger) =>
        {
            logger.LogInformation("Webhook received for webhookId {WebhookId}", webhookId);

            // Find the running Telegram provider matching this webhookId
            var match = connectionManager.GetProviders()
                .Select(p => p.Provider as TelegramChannelProvider)
                .FirstOrDefault(p => p?.WebhookId is not null &&
                    string.Equals(p.WebhookId, webhookId, StringComparison.OrdinalIgnoreCase));

            if (match?.BotClient is null || match.Handler is null)
            {
                logger.LogWarning("Webhook: no running provider for webhookId {WebhookId}", webhookId);
                return Results.NotFound();
            }

            // Validate secret token header (constant-time comparison)
            var secretHeader = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
            var expectedSecret = match.WebhookSecret ?? string.Empty;
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(secretHeader),
                    Encoding.UTF8.GetBytes(expectedSecret)))
            {
                logger.LogWarning("Webhook: secret token mismatch for webhookId {WebhookId}", webhookId);
                return Results.Unauthorized();
            }

            // Deserialize the Telegram update
            var update = await System.Text.Json.JsonSerializer.DeserializeAsync<Update>(
                request.Body, JsonBotAPI.Options, cancellationToken: request.HttpContext.RequestAborted);

            if (update is null)
            {
                logger.LogWarning("Webhook: failed to deserialize update for webhookId {WebhookId}", webhookId);
                return Results.BadRequest();
            }

            logger.LogInformation("Webhook: processing update {UpdateId} for webhookId {WebhookId}", update.Id, webhookId);

            // Process asynchronously — don't block Telegram
            var sender = match.CreateSender();
            var handler = match.Handler;
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler.HandleUpdateAsync(sender, update, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Telegram webhook handler failed for update {UpdateId} on webhookId {WebhookId}",
                        update.Id, webhookId);
                }
            });

            return Results.Ok();
        }).AllowAnonymous(); // Telegram can't send auth headers — secret token validation provides protection
    }
}
