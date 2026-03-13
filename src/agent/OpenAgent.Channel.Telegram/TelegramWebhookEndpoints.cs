using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using Telegram.Bot.Types;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Maps the Telegram webhook endpoint that receives updates from the Telegram Bot API.
/// Routes by connection ID so multiple Telegram bots can each receive webhooks.
/// </summary>
public static class TelegramWebhookEndpoints
{
    /// <summary>
    /// Maps POST /api/connections/{connectionId}/webhook/telegram — receives updates from Telegram,
    /// validates the secret token, and processes the update asynchronously.
    /// </summary>
    public static void MapTelegramWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/connections/{connectionId}/webhook/telegram", async (
            string connectionId,
            HttpRequest request,
            IConnectionManager connectionManager,
            ILogger<TelegramChannelProvider> logger) =>
        {
            // Look up the running provider for this connection
            var provider = connectionManager.GetProvider(connectionId) as TelegramChannelProvider;
            if (provider?.BotClient is null || provider.Handler is null)
                return Results.NotFound();

            // Validate secret token header (constant-time comparison)
            var secretHeader = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
            var expectedSecret = provider.WebhookSecret ?? string.Empty;
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(secretHeader),
                    Encoding.UTF8.GetBytes(expectedSecret)))
                return Results.Unauthorized();

            // Deserialize the Telegram update
            var update = await JsonSerializer.DeserializeAsync<Update>(
                request.Body, cancellationToken: request.HttpContext.RequestAborted);

            if (update is null)
                return Results.BadRequest();

            // Process asynchronously — don't block Telegram
            var sender = provider.CreateSender();
            var handler = provider.Handler;
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler.HandleUpdateAsync(sender, update, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Telegram webhook handler failed for update {UpdateId} on connection {ConnectionId}",
                        update.Id, connectionId);
                }
            });

            return Results.Ok();
        }).AllowAnonymous();
    }
}
