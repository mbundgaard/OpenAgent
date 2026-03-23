using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>Response model for the WhatsApp QR pairing endpoint.</summary>
public sealed record WhatsAppQrResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("qr")] string? Qr);

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
            IConnectionManager connectionManager) =>
        {
            var provider = connectionManager.GetProvider(connectionId) as WhatsAppChannelProvider;
            if (provider is null)
                return Results.NotFound(new WhatsAppQrResponse("error", null));

            var (status, qrData) = await provider.GetQrAsync(TimeSpan.FromSeconds(30));

            return Results.Ok(new WhatsAppQrResponse(status.ToString().ToLowerInvariant(), qrData));
        })
        .RequireAuthorization();

        return app;
    }
}
