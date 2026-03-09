using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Telegram.Bot.Types;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Maps the Telegram webhook endpoint that receives updates from the Telegram Bot API.
/// </summary>
public static class TelegramWebhookEndpoints
{
    /// <summary>
    /// Maps POST /api/telegram/webhook — receives updates from Telegram,
    /// validates the secret token, and processes the update asynchronously.
    /// </summary>
    public static void MapTelegramWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/telegram/webhook", async (
            HttpRequest request,
            TelegramChannelProvider channelProvider) =>
        {
            // Channel not started — nothing to do
            if (channelProvider.BotClient is null || channelProvider.Handler is null)
                return Results.NotFound();

            // Validate secret token header
            var secretHeader = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
            if (secretHeader != channelProvider.WebhookSecret)
                return Results.Unauthorized();

            // Deserialize the Telegram update
            var update = await JsonSerializer.DeserializeAsync<Update>(
                request.Body, cancellationToken: request.HttpContext.RequestAborted);

            if (update is null)
                return Results.BadRequest();

            // Process asynchronously — don't block Telegram
            var sender = channelProvider.CreateSender();
            var handler = channelProvider.Handler;
            _ = Task.Run(() => handler.HandleUpdateAsync(sender, update, CancellationToken.None));

            return Results.Ok();
        }).AllowAnonymous();
    }
}
