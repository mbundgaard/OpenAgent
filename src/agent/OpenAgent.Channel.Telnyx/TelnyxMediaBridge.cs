using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Per-call bridge between the Telnyx Media Streaming WebSocket and an
/// <see cref="IVoiceSession"/>. Decodes inbound a-law frames into the session, encodes outbound
/// <see cref="AudioDelta"/> events back as Telnyx media frames, and registers itself in the
/// <see cref="TelnyxBridgeRegistry"/> for the lifetime of the call. Owns barge-in (clear +
/// CancelResponseAsync on SpeechStarted) and the agent-initiated hangup state machine
/// (SetPendingHangup with 500ms early-exit + 5s hard cap).
/// </summary>
public sealed class TelnyxMediaBridge : IAsyncDisposable, ITelnyxBridge
{
    private readonly TelnyxChannelProvider _provider;
    private readonly PendingBridge _pending;
    private readonly WebSocket _ws;
    private readonly ILogger<TelnyxMediaBridge> _logger;
    private readonly CancellationTokenSource _cts;
    private IVoiceSession? _session;
    // Agent-initiated hangup state (Task 21). EndCallTool calls SetPendingHangup; the bridge
    // waits for the farewell audio to complete before invoking Telnyx HangupAsync, with a 500ms
    // early-exit when no audio flows and a 5s hard fallback if AudioDone never arrives.
    private bool _pendingHangup;
    private bool _audioObservedSinceFlag;
    private CancellationTokenSource? _hangupTimerCts;
    // Thinking-sound pump state. Active count is incremented on VoiceToolCallStarted and
    // decremented on VoiceToolCallCompleted; the pump runs while the count is non-zero so
    // overlapping tool calls don't cut the audio short. WriteLoopAsync owns these — no lock
    // needed, all mutation is on the same task.
    private int _activeToolCalls;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

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

            // Register with the global voice session manager so skill tools (and anything else
            // that consults IVoiceSessionManager) can find this call's session by conversation id
            // — required for mid-call system-prompt refresh after activate_skill.
            _provider.VoiceSessionManager.RegisterSession(_pending.ConversationId, _session);

            // Seed the realtime session with prior conversation context. Realtime APIs don't
            // replay history on connect — we have to send it explicitly. Two pieces:
            //   1. The compaction summary (long-term context) is already in the system prompt
            //      via BuildSessionConfig — nothing to do here.
            //   2. Live post-cut messages are sent as ONE synthetic user item containing a
            //      transcript blob. AddUserMessageAsync alone does not trigger a response.
            var transcript = BuildHistoryTranscript(
                _provider.ConversationStore.GetMessages(_pending.ConversationId));
            if (transcript is not null)
                await _session.AddUserMessageAsync(transcript, _cts.Token);

            // Kick the model to speak first. The synthetic prompt is realtime-only — it goes to
            // the live session but is NOT persisted to conversation history (it's a one-shot
            // trigger, not a real turn). The model has its name/identity from the system prompt;
            // this just signals "the call connected, please greet them".
            var caller = conversation.DisplayName ?? "unknown";
            await _session.AddUserMessageAsync(
                $"[Phone call connected. Caller: {caller}. Please greet them and introduce yourself.]",
                _cts.Token);
            await _session.RequestResponseAsync(_cts.Token);

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
            _provider.VoiceSessionManager.UnregisterSession(_pending.ConversationId);
            if (_session is not null)
                await _session.DisposeAsync();
            _provider.BridgeRegistry.Unregister(_pending.ConversationId);
        }
    }

    /// <summary>
    /// Renders post-cut user/assistant text turns as a single transcript blob, wrapped in
    /// <c>&lt;conversation_history&gt;</c> tags. Skips tool-call/result messages and any messages
    /// without text content — those don't survive the cross-modal text-to-voice translation
    /// cleanly, and the realtime model doesn't need them to follow the gist of recent turns.
    /// Returns null when there's nothing to seed.
    /// </summary>
    private static string? BuildHistoryTranscript(IReadOnlyList<Message> messages)
    {
        var lines = new List<string>(messages.Count);
        foreach (var m in messages)
        {
            if (m.Role == "tool") continue;
            if (string.IsNullOrWhiteSpace(m.Content)) continue;
            if (m.Role != "user" && m.Role != "assistant") continue;
            lines.Add($"{m.Role}: {m.Content!.Trim()}");
        }

        if (lines.Count == 0) return null;
        return "<conversation_history>\n" + string.Join("\n", lines) + "\n</conversation_history>";
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
    /// <see cref="AudioDelta"/> as media frames, runs the barge-in flush on SpeechStarted, and
    /// the agent-initiated hangup completion on <see cref="AudioDone"/> when
    /// <see cref="SetPendingHangup"/> has been called.
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
                        // Also stop the thinking pump (a barge-in implicitly aborts any in-flight tool work).
                        StopPump();
                        _activeToolCalls = 0;
                        await SendTextAsync(TelnyxMediaFrame.ComposeClear(), ct);
                        await _session!.CancelResponseAsync(ct);
                        break;
                    case VoiceToolCallStarted:
                        // Start the thinking-sound pump on the first concurrent tool call so the
                        // caller hears something while the tool runs (HTTP, shell, etc. can take
                        // seconds). Subsequent overlapping calls just bump the counter.
                        if (_activeToolCalls++ == 0)
                            StartPump();
                        break;
                    case VoiceToolCallCompleted:
                        // Last tool finishes → pump stops. Guarded so spurious Completed events
                        // (e.g. from a cancelled tool) can't drive the counter below zero.
                        if (_activeToolCalls > 0 && --_activeToolCalls == 0)
                            StopPump();
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

    /// <summary>
    /// Starts the thinking-sound pump on a background task. Idempotent — re-entrant calls while
    /// a pump is already running are no-ops. Caller must have already incremented the active-tool
    /// counter; the pump itself doesn't track that.
    /// </summary>
    private void StartPump()
    {
        if (_pumpTask is not null && !_pumpTask.IsCompleted) return;
        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _pumpTask = PumpAsync(_pumpCts.Token);
    }

    /// <summary>
    /// Cancels the running pump (if any) without waiting for it to finish — the pump task
    /// exits on its next yield. We deliberately don't await here to avoid blocking the
    /// WriteLoop while audio events are queued behind us.
    /// </summary>
    private void StopPump()
    {
        try { _pumpCts?.Cancel(); }
        catch { /* CTS may already be disposed */ }
    }

    /// <summary>
    /// Streams the embedded thinking clip in a 20ms-frame loop until cancelled. 8 kHz mono A-law
    /// means 160 bytes per 20ms frame; we wrap from the start of the clip when we hit the end so
    /// the sound continues for as long as the tool takes. Errors during send are swallowed — the
    /// pump is best-effort and must not take the call down on its own.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        const int frameBytes = 160;            // 20ms @ 8 kHz mono A-law
        var frameInterval = TimeSpan.FromMilliseconds(20);
        var clip = ThinkingClip.Bytes;

        var offset = 0;
        var buffer = new byte[frameBytes];
        var nextSendAt = DateTimeOffset.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                // Copy the next frame, wrapping at clip end. Two-segment copy when the
                // remaining tail is shorter than a full frame.
                var remaining = clip.Length - offset;
                if (remaining >= frameBytes)
                {
                    Array.Copy(clip, offset, buffer, 0, frameBytes);
                    offset += frameBytes;
                }
                else
                {
                    Array.Copy(clip, offset, buffer, 0, remaining);
                    Array.Copy(clip, 0, buffer, remaining, frameBytes - remaining);
                    offset = frameBytes - remaining;
                }
                if (offset >= clip.Length) offset = 0;

                try { await SendTextAsync(TelnyxMediaFrame.ComposeMedia(buffer), ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Thinking pump send failed; pump exiting");
                    return;
                }

                // Pace at 50 Hz — sleep for whatever's left until the next 20ms tick.
                nextSendAt += frameInterval;
                var sleep = nextSendAt - DateTimeOffset.UtcNow;
                if (sleep > TimeSpan.Zero)
                {
                    try { await Task.Delay(sleep, ct); }
                    catch (OperationCanceledException) { return; }
                }
                else
                {
                    // We're behind schedule (probably the send blocked) — reset baseline so we
                    // don't burn CPU catching up.
                    nextSendAt = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
    }

    public async ValueTask DisposeAsync()
    {
        StopPump();
        if (_pumpTask is not null)
        {
            try { await _pumpTask; }
            catch { /* pump exits on its own; we just want it gone before dispose */ }
        }
        _pumpCts?.Dispose();
        _cts.Dispose();
    }
}
