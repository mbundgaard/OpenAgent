using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Maps the Telnyx Media Streaming WebSocket route. Telnyx connects here per call after we
/// answer the inbound call and start streaming via the Call Control API. The route is keyed by
/// the same per-connection <c>webhookId</c> as the lifecycle webhook so multiple Telnyx
/// connections can coexist; the <c>?call=</c> query string carries the Call Control ID we
/// previously registered as a pending bridge.
/// </summary>
public static class TelnyxStreamingEndpoint
{
    /// <summary>
    /// Maps GET (WebSocket upgrade) /api/webhook/telnyx/{webhookId}/stream. Anonymous because
    /// Telnyx authenticates via the Call Control ID we already stamped into the streaming URL —
    /// there's no API key on the upgrade request. All error paths report through WebSocket Close
    /// frames after a successful upgrade so clients only need to handle one shape of error.
    /// </summary>
    public static WebApplication MapTelnyxStreamingEndpoint(this WebApplication app)
    {
        app.Map("/api/webhook/telnyx/{webhookId}/stream", async (
            string webhookId,
            HttpContext context,
            IConnectionManager connectionManager,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            // Accept the WebSocket FIRST so all error paths report through WS Close frames rather
            // than HTTP status codes during upgrade — clients (including Telnyx) only need to
            // handle one shape of error.
            var ws = await context.WebSockets.AcceptWebSocketAsync();

            var provider = connectionManager.GetProviders()
                .Select(p => p.Provider)
                .OfType<TelnyxChannelProvider>()
                .FirstOrDefault(p => p.Options.WebhookId == webhookId);
            if (provider is null)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "unknown webhook", CancellationToken.None);
                return;
            }

            var callControlId = context.Request.Query["call"].ToString();
            if (string.IsNullOrWhiteSpace(callControlId) ||
                !provider.TryDequeuePending(callControlId, out var pending) ||
                pending is null)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "unknown call", CancellationToken.None);
                return;
            }

            await using var bridge = new TelnyxMediaBridge(
                provider, pending, ws,
                loggerFactory.CreateLogger<TelnyxMediaBridge>(),
                ct);
            await bridge.RunAsync();

            // Politely close the WebSocket if either side hasn't already; the bridge's read loop
            // returns on Close from the peer, but a server-driven exit (e.g. session disposal)
            // leaves the socket open and the client waiting for the close handshake.
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch { /* best-effort close */ }
            }
        }).AllowAnonymous();

        return app;
    }
}
