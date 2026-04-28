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
            "call.initiated"    => await OnCallInitiated(provider, env, loggerFactory, ct),
            "call.hangup"       => await OnCallHangup(provider, env, ct),
            "streaming.started" => Results.Ok(),
            "streaming.stopped" => Results.Ok(),
            "streaming.failed"  => await OnStreamingFailed(provider, env, ct),
            _ => Results.Ok(),
        };
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

        // Allowlist: empty list means "allow all"
        if (provider.Options.AllowedNumbers.Count > 0 && !provider.Options.AllowedNumbers.Contains(from))
        {
            await provider.CallControlClient.HangupAsync(callControlId, ct);
            return Results.Ok();
        }

        // Conversation lookup — keyed on caller E.164 so repeat callers get a stable history
        var conv = provider.ConversationStore.FindOrCreateChannelConversation(
            channelType: "telnyx",
            connectionId: provider.ConnectionId,
            channelChatId: from,
            source: "telnyx",
            textProvider: provider.AgentConfig.TextProvider,
            textModel: provider.AgentConfig.TextModel,
            voiceProvider: provider.AgentConfig.VoiceProvider,
            voiceModel: provider.AgentConfig.VoiceModel);

        if (!string.Equals(conv.DisplayName, from, StringComparison.Ordinal))
            provider.ConversationStore.UpdateDisplayName(conv.Id, from);

        // Answer the call
        await provider.CallControlClient.AnswerAsync(callControlId, ct);

        // Register pending bridge BEFORE issuing streaming_start so the WS endpoint can pick it up
        var cts = new CancellationTokenSource();
        var pending = new PendingBridge(callControlId, conv.Id, conv.VoiceProvider, cts);
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
        [JsonPropertyName("connection_id")] public string? ConnectionId { get; set; }
        [JsonPropertyName("from")] public string? From { get; set; }
        [JsonPropertyName("to")] public string? To { get; set; }
    }
}
