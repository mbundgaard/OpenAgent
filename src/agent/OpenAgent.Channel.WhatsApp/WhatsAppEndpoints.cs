using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>Response model for the WhatsApp QR pairing endpoint.</summary>
public sealed record WhatsAppQrResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("qr")] string? Qr,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>
/// Registers WhatsApp-specific HTTP endpoints.
/// </summary>
public static class WhatsAppEndpoints
{
    /// <summary>Maps the WhatsApp QR code pairing endpoint.</summary>
    public static WebApplication MapWhatsAppEndpoints(this WebApplication app)
    {
        app.MapGet("/api/connections/{connectionId}/whatsapp/qr", async (
            string connectionId,
            IConnectionManager connectionManager,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("WhatsAppEndpoints");
            var rawProvider = connectionManager.GetProvider(connectionId);
            var provider = rawProvider as WhatsAppChannelProvider;
            if (provider is null)
            {
                logger.LogWarning("QR requested for connection {ConnectionId} but provider is {State}",
                    connectionId, rawProvider is null ? "not running" : $"wrong type ({rawProvider.GetType().Name})");
                return Results.NotFound(new WhatsAppQrResponse("error", null, "No WhatsApp connection found or not started"));
            }

            var (status, qrData, error) = await provider.GetQrAsync(TimeSpan.FromSeconds(30));

            return Results.Ok(new WhatsAppQrResponse(status.ToString().ToLowerInvariant(), qrData, error));
        })
        .RequireAuthorization();

        return app;
    }
}
