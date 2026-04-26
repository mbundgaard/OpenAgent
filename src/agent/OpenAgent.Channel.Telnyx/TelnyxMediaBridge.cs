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
/// <see cref="TelnyxBridgeRegistry"/> for the lifetime of the call. Owns barge-in (clear +
/// CancelResponseAsync on SpeechStarted), the thinking-clip pump during tool calls, and the
/// agent-initiated hangup state machine (SetPendingHangup with 500ms early-exit + 5s hard cap).
/// </summary>
public sealed class TelnyxMediaBridge : IAsyncDisposable, ITelnyxBridge
{
    private readonly TelnyxChannelProvider _provider;
    private readonly PendingBridge _pending;
    private readonly WebSocket _ws;
    private readonly ILogger<TelnyxMediaBridge> _logger;
    private readonly CancellationTokenSource _cts;
    private IVoiceSession? _session;
    // Thinking-clip pump state. _activeToolCalls is ref-counted so nested tool calls (e.g.
    // web_fetch invoking extract_text) keep the pump running until the LAST completion.
    private CancellationTokenSource? _pumpCts;
    private int _activeToolCalls;
    // Agent-initiated hangup state (Task 21). EndCallTool calls SetPendingHangup; the bridge
    // waits for the farewell audio to complete before invoking Telnyx HangupAsync, with a 500ms
    // early-exit when no audio flows and a 5s hard fallback if AudioDone never arrives.
    private bool _pendingHangup;
    private bool _audioObservedSinceFlag;
    private CancellationTokenSource? _hangupTimerCts;

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

            // Telnyx delivers a-law / 8 kHz on both directions for European calls (PCMA is the
            // default termination codec on +45 numbers). Pure byte-pipe: matches what the carrier
            // sends inbound and what we asked Telnyx to use outbound (stream_bidirectional_codec).
            _session = await voiceProvider.StartSessionAsync(
                conversation,
                new VoiceSessionOptions("g711_alaw", 8000),
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
    /// Pumps voice events from the session out to the Telnyx WebSocket. Forwards
    /// <see cref="AudioDelta"/> as media frames, runs the barge-in flush on SpeechStarted, the
    /// thinking-clip pump on tool start/complete, and the agent-initiated hangup completion on
    /// <see cref="AudioDone"/> when <see cref="SetPendingHangup"/> has been called.
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
                        // Mark farewell audio so the hangup timer's 500ms early-exit doesn't fire.
                        if (_pendingHangup) _audioObservedSinceFlag = true;
                        await SendTextAsync(TelnyxMediaFrame.ComposeMedia(audio.Audio.Span), ct);
                        break;
                    case AudioDone:
                        // Clean farewell case: agent finished speaking after EndCallTool fired.
                        if (_pendingHangup && _audioObservedSinceFlag)
                        {
                            try { _hangupTimerCts?.Cancel(); } catch { /* CTS may already be disposed */ }
                            await DoHangupAsync();
                        }
                        break;
                    case SpeechStarted:
                        // Barge-in: flush any buffered TTS at Telnyx and cancel the LLM response so
                        // the model stops generating into a user that's already talking over it.
                        await SendTextAsync(TelnyxMediaFrame.ComposeClear(), ct);
                        await _session!.CancelResponseAsync(ct);
                        break;
                    case VoiceToolCallStarted:
                        // Tool execution can take seconds — start pumping the procedural µ-law
                        // thinking clip so the caller hears something instead of dead air.
                        StartPump(ct);
                        break;
                    case VoiceToolCallCompleted:
                        // Last completion stops the pump and flushes any residual buffered audio
                        // at Telnyx with a clear frame; nested calls keep the pump alive.
                        await StopPumpAsync(ct);
                        break;
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

    /// <summary>
    /// Starts the thinking-clip pump on the first <see cref="VoiceToolCallStarted"/>. Nested
    /// tool calls increment the ref counter without restarting the timer.
    /// </summary>
    private void StartPump(CancellationToken ct)
    {
        // Ref-count: only the first VoiceToolCallStarted starts the timer; subsequent ones are nested calls.
        if (Interlocked.Increment(ref _activeToolCalls) > 1) return;
        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    /// <summary>
    /// Decrements the ref counter on <see cref="VoiceToolCallCompleted"/>; the LAST completion
    /// cancels the pump and emits a clear frame to flush any buffered audio at Telnyx.
    /// </summary>
    private async Task StopPumpAsync(CancellationToken ct)
    {
        // Only stop on the last completion — earlier ones leave the pump running for nested calls.
        if (Interlocked.Decrement(ref _activeToolCalls) > 0) return;
        if (_pumpCts is { } cts) await cts.CancelAsync();
        if (_ws.State == WebSocketState.Open)
            await SendTextAsync(TelnyxMediaFrame.ComposeClear(), ct);
    }

    /// <summary>
    /// Drift-free 20ms loop that streams 160-byte µ-law slices of the procedural thinking clip
    /// out as Telnyx media frames, wrapping back to the start when the clip ends. Cancelled by
    /// <see cref="StopPumpAsync"/> when the last tool call completes.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        var clip = _provider.ThinkingClip;
        var pos = 0;
        const int frameSize = 160; // 20ms at 8kHz µ-law
        var period = TimeSpan.FromMilliseconds(20);

        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_ws.State != WebSocketState.Open) break;
                var slice = clip.AsMemory(pos, Math.Min(frameSize, clip.Length - pos));
                await SendTextAsync(TelnyxMediaFrame.ComposeMedia(slice.Span), ct);
                pos = (pos + frameSize) % clip.Length;
            }
        }
        catch (OperationCanceledException) { /* expected on tool completion */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Telnyx thinking pump WS send failed");
        }
    }

    /// <summary>
    /// Marks this bridge as ready to hang up after the agent's farewell audio. Invoked by
    /// <c>EndCallTool</c> (Task 23) via <see cref="TelnyxBridgeRegistry"/>. Three branches resolve
    /// the hangup: AudioDone after observed audio (clean), 500ms with no audio (model already
    /// done or didn't speak), or 5s hard fallback (model misbehaves / API stalls).
    /// </summary>
    public void SetPendingHangup()
    {
        _pendingHangup = true;
        // Reset every time so a second SetPendingHangup attempt can't be shortcut by stale state.
        _audioObservedSinceFlag = false;
        _hangupTimerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = HangupTimerAsync(_hangupTimerCts.Token);
    }

    /// <summary>
    /// Drives the two-stage hangup fallback: 500ms early-exit if no audio flowed since
    /// <see cref="SetPendingHangup"/> (model already finished or never spoke), then a hard 5s
    /// total cap if audio started but AudioDone never arrives.
    /// </summary>
    private async Task HangupTimerAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            // Early-exit window: if no audio has flowed since SetPendingHangup, the agent isn't going to speak.
            if (_pendingHangup && !_audioObservedSinceFlag)
            {
                await DoHangupAsync();
                return;
            }
            // Hard fallback: 4500ms more = 5s total. Catches model stalls.
            await Task.Delay(TimeSpan.FromMilliseconds(4500), ct);
            if (_pendingHangup) await DoHangupAsync();
        }
        catch (OperationCanceledException) { /* AudioDone path completed first, or session closed */ }
    }

    /// <summary>
    /// Idempotent hangup invocation. The first call flips the flag and POSTs to Telnyx; concurrent
    /// callers (timer racing AudioDone) must no-op. HangupAsync itself is idempotent server-side
    /// (404/410 treated as success) so duplicate POSTs are safe — but we still gate to avoid noise.
    /// </summary>
    private async Task DoHangupAsync()
    {
        if (!_pendingHangup) return; // Idempotent — concurrent paths must no-op after the first.
        _pendingHangup = false;
        try
        {
            // Best-effort: hangup uses default token because the endpoint is idempotent and we
            // want it to complete even if the bridge is already tearing down.
            await _provider.CallControlClient.HangupAsync(_pending.CallControlId, default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telnyx HangupAsync failed for call {CallControlId}", _pending.CallControlId);
        }
        finally
        {
            try { _hangupTimerCts?.Cancel(); } catch { /* CTS may already be disposed */ }
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
