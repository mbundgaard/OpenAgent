using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Voice;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Per-call bridge between the Telnyx Media Streaming WebSocket and an
/// <see cref="IVoiceSession"/>. Decodes inbound µ-law frames into the session, encodes outbound
/// <see cref="AudioDelta"/> events back as Telnyx media frames, and registers itself in the
/// <see cref="TelnyxBridgeRegistry"/> for the lifetime of the call. Barge-in, thinking-clip
/// pumping, and hangup land in subsequent tasks (19/20/21).
/// </summary>
public sealed class TelnyxMediaBridge : IAsyncDisposable
{
    private readonly TelnyxChannelProvider _provider;
    private readonly PendingBridge _pending;
    private readonly WebSocket _ws;
    private readonly ILogger<TelnyxMediaBridge> _logger;
    private readonly CancellationTokenSource _cts;
    private IVoiceSession? _session;

    public TelnyxMediaBridge(
        TelnyxChannelProvider provider,
        PendingBridge pending,
        WebSocket ws,
        ILogger<TelnyxMediaBridge> logger,
        CancellationToken ct)
    {
        _provider = provider;
        _pending = pending;
        _ws = ws;
        _logger = logger;
        // Linked CTS so either the request token or our own teardown can cancel both loops.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>
    /// Opens the voice session, runs the read+write loops in parallel, and tears everything down
    /// on first completion of either loop. Registers in the bridge registry for the call's
    /// lifetime so the EndCallTool can find this bridge by conversation id.
    /// </summary>
    public async Task RunAsync()
    {
        _provider.BridgeRegistry.Register(_pending.ConversationId, this);
        try
        {
            var voiceProvider = _provider.VoiceProviderResolver(_pending.VoiceProviderKey);
            var conversation = _provider.ConversationStore.Get(_pending.ConversationId)
                ?? throw new InvalidOperationException(
                    $"Conversation {_pending.ConversationId} missing — cannot start voice session.");

            // Telnyx delivers µ-law / 8 kHz on both directions.
            _session = await voiceProvider.StartSessionAsync(
                conversation,
                new VoiceSessionOptions("g711_ulaw", 8000),
                _cts.Token);

            // First loop to finish wins — the other gets cancelled in the finally block.
            await Task.WhenAny(ReadLoopAsync(_cts.Token), WriteLoopAsync(_cts.Token));
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telnyx bridge errored for conversation {ConversationId}",
                _pending.ConversationId);
        }
        finally
        {
            await _cts.CancelAsync();
            if (_session is not null)
                await _session.DisposeAsync();
            _provider.BridgeRegistry.Unregister(_pending.ConversationId);
        }
    }

    /// <summary>
    /// Reads JSON frames off the Telnyx WebSocket and routes them. Inbound media payloads decode
    /// to bytes and stream into the voice session; outbound-track media frames are dropped (they
    /// can appear if stream_track is misconfigured); dtmf is debug-logged; stop ends the loop;
    /// start logs the negotiated codec.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                TelnyxMediaFrame frame;
                try
                {
                    frame = TelnyxMediaFrame.Parse(sb.ToString());
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Telnyx malformed frame, ignoring");
                    continue;
                }

                switch (frame.Event)
                {
                    case "media" when frame.Media is { Track: "inbound" }:
                        // Forward decoded µ-law bytes into the voice session.
                        await _session!.SendAudioAsync(frame.Media.PayloadBytes, ct);
                        break;
                    case "media":
                        // Outbound or unknown track — drop it (defensive against stream_track misconfig).
                        break;
                    case "dtmf":
                        _logger.LogDebug("Telnyx DTMF digit {Digit}", frame.Dtmf?.Digit);
                        break;
                    case "stop":
                        return;
                    case "start":
                        _logger.LogInformation(
                            "Telnyx stream started, format={Encoding}/{Rate}",
                            frame.Start?.MediaFormat.Encoding, frame.Start?.MediaFormat.SampleRate);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on teardown */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Telnyx WebSocket lost for conversation {ConversationId}",
                _pending.ConversationId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Pumps voice events from the session out to the Telnyx WebSocket. Currently forwards
    /// <see cref="AudioDelta"/> only; later tasks add SpeechStarted (barge-in), tool-call
    /// thinking pumps, and AudioDone-driven hangup.
    /// </summary>
    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _session!.ReceiveEventsAsync(ct))
            {
                if (_ws.State != WebSocketState.Open) break;
                switch (evt)
                {
                    case AudioDelta audio:
                        await SendTextAsync(TelnyxMediaFrame.ComposeMedia(audio.Audio.Span), ct);
                        break;
                    case SpeechStarted:
                        // Barge-in: flush any buffered TTS at Telnyx and cancel the LLM response so
                        // the model stops generating into a user that's already talking over it.
                        await SendTextAsync(TelnyxMediaFrame.ComposeClear(), ct);
                        await _session!.CancelResponseAsync(ct);
                        break;
                        // Tasks 20-22: VoiceToolCallStarted (thinking clip), AudioDone with pending
                        // hangup, etc.
                }
            }
        }
        catch (OperationCanceledException) { /* expected on teardown */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Telnyx WebSocket send failed for conversation {ConversationId}",
                _pending.ConversationId);
        }
    }

    /// <summary>Sends a UTF-8 text frame to the Telnyx WebSocket. Used for media + clear frames.</summary>
    private Task SendTextAsync(string json, CancellationToken ct) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    public ValueTask DisposeAsync()
    {
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
