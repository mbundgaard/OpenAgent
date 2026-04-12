using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Maps the Telnyx TeXML webhook endpoints that receive inbound call events.
/// Routes by auto-generated webhookId so the user never needs to know connection IDs.
/// All endpoints are anonymous — Telnyx cannot send auth headers; ED25519 signature
/// verification provides the protection.
/// </summary>
public static class TelnyxWebhookEndpoints
{
    /// <summary>
    /// Maps three TeXML webhook routes:
    /// <list type="bullet">
    ///   <item>POST /api/webhook/telnyx/{webhookId}/voice — initial inbound call</item>
    ///   <item>POST /api/webhook/telnyx/{webhookId}/speech — speech-gather result</item>
    ///   <item>POST /api/webhook/telnyx/{webhookId}/status — call-status update</item>
    /// </list>
    /// </summary>
    public static WebApplication MapTelnyxWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhook/telnyx/{webhookId}/voice", HandleVoice)
           .AllowAnonymous()
           .WithName("TelnyxVoiceWebhook");

        app.MapPost("/api/webhook/telnyx/{webhookId}/speech", HandleSpeech)
           .AllowAnonymous()
           .WithName("TelnyxSpeechWebhook");

        app.MapPost("/api/webhook/telnyx/{webhookId}/status", HandleStatus)
           .AllowAnonymous()
           .WithName("TelnyxStatusWebhook");

        return app;
    }

    private static async Task<IResult> HandleVoice(
        string webhookId,
        HttpRequest request,
        IConnectionManager connectionManager,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelnyxWebhook");
        var (provider, form) = await ReadAndVerifyAsync(webhookId, request, connectionManager, logger, ct);
        if (provider is null)
            return Results.NotFound();

        var from = form["From"].ToString();
        var to = form["To"].ToString();
        var callSid = form["CallSid"].ToString();

        var xml = await provider.Handler.HandleVoiceAsync(callSid, from, to, ct);
        return Results.Content(xml, "application/xml");
    }

    private static async Task<IResult> HandleSpeech(
        string webhookId,
        HttpRequest request,
        IConnectionManager connectionManager,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelnyxWebhook");
        var (provider, form) = await ReadAndVerifyAsync(webhookId, request, connectionManager, logger, ct);
        if (provider is null)
            return Results.NotFound();

        var from = form["From"].ToString();
        var callSid = form["CallSid"].ToString();
        // Pass string.Empty (not null) when SpeechResult is absent — handler's IsNullOrWhiteSpace guard covers it
        var speech = form["SpeechResult"].ToString();

        var xml = await provider.Handler.HandleSpeechAsync(callSid, from, speech, ct);
        return Results.Content(xml, "application/xml");
    }

    private static async Task<IResult> HandleStatus(
        string webhookId,
        HttpRequest request,
        IConnectionManager connectionManager,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelnyxWebhook");
        var (provider, form) = await ReadAndVerifyAsync(webhookId, request, connectionManager, logger, ct);
        if (provider is null)
            return Results.NotFound();

        var from = form["From"].ToString();
        var callSid = form["CallSid"].ToString();
        var status = form["CallStatus"].ToString();

        // Notify the handler on terminal call states
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            await provider.Handler.HandleHangupAsync(callSid, from, ct);
        }

        return Results.Ok();
    }

    // Shared preamble: find the provider, buffer the body, verify the ED25519 signature,
    // parse the form fields. Returns null provider on any failure (not found or auth failure).
    private static async Task<(TelnyxChannelProvider? provider, IFormCollection form)>
        ReadAndVerifyAsync(
            string webhookId,
            HttpRequest request,
            IConnectionManager connectionManager,
            ILogger logger,
            CancellationToken ct)
    {
        // Resolve the running provider matching this webhookId
        var provider = connectionManager.GetProviders()
            .Select(p => p.Provider)
            .OfType<TelnyxChannelProvider>()
            .FirstOrDefault(p => p.WebhookId == webhookId);

        if (provider is null)
        {
            logger.LogWarning("Telnyx webhook received for unknown webhookId={WebhookId}", webhookId);
            return (null, new FormCollection(null));
        }

        // Buffer the raw body so we can both verify the signature and re-parse form fields
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        var rawBody = ms.ToArray();

        // Verify ED25519 signature — skipped automatically when WebhookPublicKey is null (dev mode)
        var sig = request.Headers["Telnyx-Signature-ed25519"].ToString();
        var ts = request.Headers["Telnyx-Timestamp"].ToString();

        if (!provider.SignatureVerifier.Verify(
                provider.Options.WebhookPublicKey,
                sig,
                ts,
                rawBody,
                DateTimeOffset.UtcNow))
        {
            logger.LogWarning("Telnyx webhook for {ConnectionId} failed signature verification", provider.ConnectionId);
            return (null, new FormCollection(null));
        }

        // Re-parse the buffered body as application/x-www-form-urlencoded
        using var bodyStream = new MemoryStream(rawBody);
        using var reader = new StreamReader(bodyStream);
        var text = await reader.ReadToEndAsync(ct);
        var parsed = QueryHelpers.ParseQuery(text);
        var form = new FormCollection(parsed.ToDictionary(kv => kv.Key, kv => kv.Value));

        return (provider, form);
    }
}
