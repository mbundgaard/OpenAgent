using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Maps the Telnyx call lifecycle webhook endpoint. Handles five event_types:
/// <c>call.initiated</c>, <c>call.hangup</c>, <c>streaming.started</c>, <c>streaming.stopped</c>,
/// <c>streaming.failed</c>. Routing is keyed by per-connection <c>webhookId</c> so multiple
/// Telnyx connections can coexist behind a single host.
/// </summary>
public static class TelnyxWebhookEndpoints
{
    /// <summary>
    /// Maps POST /api/webhook/telnyx/{webhookId}/call. Anonymous because Telnyx authenticates
    /// via the ED25519 signature in the request headers, not via the host's API key.
    /// </summary>
    public static WebApplication MapTelnyxWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/webhook/telnyx/{webhookId}/call", HandleCallEvent)
           .AllowAnonymous()
           .WithName("TelnyxCallWebhook");
        return app;
    }

    private static async Task<IResult> HandleCallEvent(
        string webhookId,
        HttpRequest request,
        IConnectionManager connectionManager,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("TelnyxWebhook");

        // Find the running provider by webhookId
        var provider = connectionManager.GetProviders()
            .Select(p => p.Provider)
            .OfType<TelnyxChannelProvider>()
            .FirstOrDefault(p => p.Options.WebhookId == webhookId);
        if (provider is null) return Results.NotFound();

        // Buffer body so we can both verify signature and parse JSON
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        var rawBody = ms.ToArray();

        // Verify ED25519 signature (verifier returns true with a warning when no key configured — dev mode)
        var sig = request.Headers["Telnyx-Signature-ed25519"].ToString();
        var ts = request.Headers["Telnyx-Timestamp"].ToString();
        if (!provider.SignatureVerifier.Verify(provider.Options.WebhookPublicKey, sig, ts, rawBody, DateTimeOffset.UtcNow))
        {
            logger.LogWarning("Telnyx webhook signature failed for {ConnectionId}", provider.ConnectionId);
            return Results.Unauthorized();
        }

        // Parse the envelope. Properties carry [JsonPropertyName] attributes for the snake_case
        // wire shape, so default JsonSerializerOptions are sufficient.
        TelnyxWebhookEnvelope env;
        try
        {
            env = JsonSerializer.Deserialize<TelnyxWebhookEnvelope>(rawBody)
                  ?? throw new JsonException("null");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telnyx webhook JSON malformed");
            return Results.BadRequest();
        }

        // Validate connection_id matches our configured Call Control connection
        if (!string.Equals(env.Data?.Payload?.ConnectionId, provider.Options.CallControlAppId, StringComparison.Ordinal))
        {
            logger.LogWarning("Telnyx webhook connection_id mismatch (got {Got}, expected {Want})",
                env.Data?.Payload?.ConnectionId, provider.Options.CallControlAppId);
            return Results.Unauthorized();
        }

        // Dump the raw JSON for every accepted event. Lets us see the full Telnyx payload —
        // including fields we don't currently parse (sip_username, sip_to, custom_headers, etc.)
        // — when investigating extension routing or unfamiliar webhook shapes. Information level
        // because these aren't frequent (a few per call) and the body is genuinely useful.
        logger.LogInformation("Telnyx webhook {EventType} payload: {RawBody}",
            env.Data?.EventType, System.Text.Encoding.UTF8.GetString(rawBody));

        return env.Data?.EventType switch
        {
            "call.initiated"      => await OnCallInitiated(provider, env, loggerFactory, ct),
            "call.hangup"         => await OnCallHangup(provider, env, ct),
            "call.dtmf.received"  => OnDtmfReceived(provider, env, logger),
            "streaming.started"   => Results.Ok(),
            "streaming.stopped"   => Results.Ok(),
            "streaming.failed"    => await OnStreamingFailed(provider, env, ct),
            _ => Results.Ok(),
        };
    }

    /// <summary>
    /// Forward the digit to the bridge for extension routing. If the bridge is already running
    /// it lands on its DTMF gate channel; if the call is still pending (rare — DTMF before WS
    /// connect) it buffers on the pending entry and the bridge drains it on start. Subsequent
    /// digits within the gate window AND digits past the window are silently ignored by the bridge.
    /// Returns Ok immediately so Telnyx doesn't retry.
    /// </summary>
    private static IResult OnDtmfReceived(TelnyxChannelProvider provider, TelnyxWebhookEnvelope env, ILogger logger)
    {
        var callControlId = env.Data!.Payload!.CallControlId ?? "";
        var digit = env.Data!.Payload!.Digit ?? "";
        if (string.IsNullOrEmpty(digit) || string.IsNullOrEmpty(callControlId))
            return Results.Ok();

        if (provider.BridgeRegistry.TryGetByCallControlId(callControlId, out var bridge)
            && bridge is ITelnyxBridge typed)
        {
            typed.OnDtmfReceived(digit);
            return Results.Ok();
        }

        if (provider.TryGetPending(callControlId, out var pending) && pending is not null)
        {
            pending.PendingDtmf.Enqueue(digit);
            logger.LogDebug("Buffered DTMF digit {Digit} on pending bridge for {CallControlId}",
                digit, callControlId);
        }

        return Results.Ok();
    }

    private static async Task<IResult> OnCallInitiated(
        TelnyxChannelProvider provider,
        TelnyxWebhookEnvelope env,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var p = env.Data!.Payload!;
        var from = p.From ?? "";
        var callControlId = p.CallControlId ?? "";
        // call_session_id is what makes each call unique even when from the same caller —
        // used by the bridge to key the per-call throwaway conversation. Fallback to a GUID
        // is defensive; in practice Telnyx always provides this field.
        var callSessionId = p.CallSessionId ?? Guid.NewGuid().ToString("N");

        // Allowlist: empty list means "allow all"
        if (provider.Options.AllowedNumbers.Count > 0 && !provider.Options.AllowedNumbers.Contains(from))
        {
            await provider.CallControlClient.HangupAsync(callControlId, ct);
            return Results.Ok();
        }

        // Answer the call. Conversation creation is deferred to the bridge — it creates a
        // per-call throwaway on WS connect, which may be swapped for an extension conversation
        // if DTMF arrives within 8 seconds (extension routing).
        await provider.CallControlClient.AnswerAsync(callControlId, ct);

        // Register pending bridge BEFORE issuing streaming_start so the WS endpoint can pick it
        // up — and so DTMF that arrives before the WS connects can buffer on PendingDtmf.
        var cts = new CancellationTokenSource();
        var pending = new PendingBridge(
            CallControlId: callControlId,
            CallSessionId: callSessionId,
            From: from,
            VoiceProviderKey: provider.AgentConfig.VoiceProvider,
            Cts: cts);
        if (!provider.TryRegisterPending(callControlId, pending))
            return Results.Ok(); // duplicate event, ignore

        // Self-evict + hang up if the streaming WebSocket doesn't connect within 30 s
        var token = cts.Token;
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        token.Register(async () =>
        {
            // The WS endpoint claims the bridge by dequeueing it; if it's still here, the WS
            // never connected and we should hang up to avoid leaking the call.
            if (provider.TryDequeuePending(callControlId, out _))
            {
                try { await provider.CallControlClient.HangupAsync(callControlId, default); }
                catch { /* idempotent — best-effort cleanup */ }
            }
        });

        // Start streaming
        var wsUrl = BuildStreamUrl(provider, callControlId);
        try
        {
            await provider.CallControlClient.StreamingStartAsync(callControlId, wsUrl, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TelnyxWebhook").LogWarning(ex, "streaming_start failed; rolling back call");
            provider.TryDequeuePending(callControlId, out _);
            await provider.CallControlClient.HangupAsync(callControlId, default);
            return Results.Ok();
        }
        return Results.Ok();
    }

    private static Task<IResult> OnCallHangup(TelnyxChannelProvider provider, TelnyxWebhookEnvelope env, CancellationToken ct)
    {
        var callControlId = env.Data!.Payload!.CallControlId ?? "";
        // Pending entry cleanup; active bridge cleanup happens in the bridge itself via WS close
        provider.TryDequeuePending(callControlId, out _);
        return Task.FromResult(Results.Ok());
    }

    private static async Task<IResult> OnStreamingFailed(TelnyxChannelProvider provider, TelnyxWebhookEnvelope env, CancellationToken ct)
    {
        var callControlId = env.Data!.Payload!.CallControlId ?? "";
        provider.TryDequeuePending(callControlId, out _);
        await provider.CallControlClient.HangupAsync(callControlId, default);
        return Results.Ok();
    }

    private static string BuildStreamUrl(TelnyxChannelProvider provider, string callControlId)
    {
        var baseUrl = provider.Options.BaseUrl!.TrimEnd('/');
        var wssBase = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "wss://" + baseUrl["https://".Length..]
            : "ws://" + baseUrl["http://".Length..];
        return $"{wssBase}/api/webhook/telnyx/{provider.Options.WebhookId}/stream?call={Uri.EscapeDataString(callControlId)}";
    }

    private sealed class TelnyxWebhookEnvelope
    {
        [JsonPropertyName("data")] public TelnyxData? Data { get; set; }
    }

    private sealed class TelnyxData
    {
        [JsonPropertyName("event_type")] public string? EventType { get; set; }
        [JsonPropertyName("payload")] public TelnyxPayload? Payload { get; set; }
    }

    private sealed class TelnyxPayload
    {
        [JsonPropertyName("call_control_id")] public string? CallControlId { get; set; }
        [JsonPropertyName("call_session_id")] public string? CallSessionId { get; set; }
        [JsonPropertyName("connection_id")] public string? ConnectionId { get; set; }
        [JsonPropertyName("from")] public string? From { get; set; }
        [JsonPropertyName("to")] public string? To { get; set; }
        [JsonPropertyName("digit")] public string? Digit { get; set; }
    }
}
