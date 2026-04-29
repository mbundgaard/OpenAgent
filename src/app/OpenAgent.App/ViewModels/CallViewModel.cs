using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// Drives the active call: owns the WebSocket session loop, the audio capture/playback pipeline,
/// the call state machine, and the transcript router. A single owning task (<see cref="RunSessionLoopAsync"/>)
/// handles connect → drain → reconnect linearly so flow is never recursive. All UI mutations
/// (<see cref="State"/>, <see cref="Bubbles"/>, Shell navigation/dialogs) marshal to the main thread
/// via <see cref="MainThread.BeginInvokeOnMainThread"/> or the <c>OnMain</c> helper.
/// </summary>
[QueryProperty(nameof(ConversationId), "conversationId")]
[QueryProperty(nameof(Title), "title")]
public partial class CallViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ICallAudio _audio;
    private readonly CallStateMachine _sm = new();
    private readonly ReconnectBackoff _backoff = new();

    private IVoiceWebSocketClient? _ws;
    private CancellationTokenSource? _cts;
    private TranscriptRouter? _transcript;
    private Task? _runner;
    private bool _ended;

    [ObservableProperty] private string? _conversationId;
    [ObservableProperty] private string? _title;
    [ObservableProperty] private CallState _state;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _showTranscript;

    public ObservableCollection<TranscriptBubble> Bubbles { get; } = new();

    public CallViewModel(IServiceProvider services, ICallAudio audio)
    {
        _services = services;
        _audio = audio;
        _audio.OnPcmCaptured += pcm =>
        {
            // Fire-and-forget: SendAudioAsync serializes internally via SemaphoreSlim.
            var ws = _ws;
            var token = _cts?.Token ?? CancellationToken.None;
            if (ws is null || token.IsCancellationRequested) return;
            _ = ws.SendAudioAsync(pcm, token);
        };
    }

    [RelayCommand]
    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _ended = false;
        _transcript = new TranscriptRouter(
            onAppend: (src, t) => MainThread.BeginInvokeOnMainThread(() => Bubbles.Add(new TranscriptBubble(src, t))),
            onUpdateLast: (t) => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (Bubbles.Count > 0) Bubbles[^1] = Bubbles[^1] with { Text = t };
            }));

        _runner = Task.Run(() => RunSessionLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Owns the entire session: each iteration is one connect → drain → optional reconnect.</summary>
    private async Task RunSessionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_ended)
        {
            try
            {
                SetState(CallState.Connecting, viaMachine: sm => sm.OnConnecting());

                try
                {
                    _ws = _services.GetRequiredService<IVoiceWebSocketClient>();
                    await _ws.ConnectAsync(ConversationId!, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    await TeardownAsync();
                    if (!await TryReconnectOrFinishAsync(ex.Message, authRejected: false, ct)) return;
                    continue;
                }

                _backoff.Reset();
                var disconnect = await DrainFramesAsync(ct);
                await TeardownAsync();

                if (ct.IsCancellationRequested || _ended) return;

                if (disconnect.AuthRejected)
                {
                    await OnMain(() => Shell.Current.DisplayAlert("Authentication failed",
                        "Token rejected by agent. Please reconfigure.", "OK"));
                    await OnMain(() => Shell.Current.GoToAsync("//onboarding"));
                    return;
                }

                if (!await TryReconnectOrFinishAsync(disconnect.Reason ?? "Connection lost", authRejected: false, ct)) return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                await TeardownAsync();
                await OnMain(() => Shell.Current.DisplayAlert("Call failed", ex.Message, "OK"));
                await OnMain(() => Shell.Current.GoToAsync(".."));
                return;
            }
        }
    }

    /// <summary>Reads frames until the WebSocket disconnects. Returns the disconnect frame.</summary>
    private async Task<VoiceFrame.Disconnected> DrainFramesAsync(CancellationToken ct)
    {
        var ws = _ws!;
        await foreach (var frame in ws.ReadFramesAsync(ct))
        {
            switch (frame)
            {
                case VoiceFrame.EventFrame { Event: VoiceEvent.SessionReady ready }:
                    if (!string.Equals(ready.InputCodec, "pcm16", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(ready.OutputCodec, "pcm16", StringComparison.OrdinalIgnoreCase))
                    {
                        await OnMain(() => Shell.Current.DisplayAlert("Codec mismatch",
                            $"Agent announced {ready.InputCodec}/{ready.OutputCodec}; this client only handles pcm16.", "OK"));
                        return new VoiceFrame.Disconnected("Unsupported codec", AuthRejected: false);
                    }
                    await _audio.StartAsync(ready.InputSampleRate, ct);
                    SetState(CallState.Listening, viaMachine: sm => sm.Apply(ready));
                    break;

                case VoiceFrame.EventFrame ef:
                    if (ef.Event is VoiceEvent.SpeechStarted) _audio.FlushPlayback();
                    if (ef.Event is VoiceEvent.TranscriptDelta td) _transcript!.OnDelta(td.Source, td.Text);
                    if (ef.Event is VoiceEvent.TranscriptDone) _transcript!.OnDone();
                    SetState(_sm.State, viaMachine: sm => sm.Apply(ef.Event));
                    if (ef.Event is VoiceEvent.Error err)
                        await OnMain(() => Shell.Current.DisplayAlert("Voice error", err.Message, "OK"));
                    break;

                case VoiceFrame.AudioFrame af:
                    _audio.EnqueuePlayback(af.Pcm16);
                    SetState(CallState.AssistantSpeaking, viaMachine: sm => sm.OnAudioReceived());
                    break;

                case VoiceFrame.Disconnected d:
                    return d;
            }
        }
        return new VoiceFrame.Disconnected("Stream ended", AuthRejected: false);
    }

    private async Task<bool> TryReconnectOrFinishAsync(string reason, bool authRejected, CancellationToken ct)
    {
        if (authRejected) return false;
        if (_backoff.GiveUp || ct.IsCancellationRequested)
        {
            await OnMain(() => Shell.Current.DisplayAlert("Disconnected", reason, "OK"));
            await OnMain(() => Shell.Current.GoToAsync(".."));
            return false;
        }
        SetState(CallState.Reconnecting, viaMachine: sm => sm.OnReconnecting());
        try { await Task.Delay(_backoff.NextDelay(), ct); }
        catch (OperationCanceledException) { return false; }
        return true;
    }

    private async Task TeardownAsync()
    {
        var ws = _ws;
        _ws = null;
        if (ws is not null)
        {
            try { await ws.DisposeAsync(); } catch { }
        }
        try { await _audio.StopAsync(); } catch { }
    }

    [RelayCommand]
    public void ToggleMute()
    {
        Muted = !Muted;
        _audio.SetMuted(Muted);
    }

    [RelayCommand]
    public async Task EndAsync()
    {
        _ended = true;
        _cts?.Cancel();
        await TeardownAsync();
        if (_runner is not null) { try { await _runner; } catch { } }
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    public void ToggleTranscript() => ShowTranscript = !ShowTranscript;

    private void SetState(CallState newState, Action<CallStateMachine> viaMachine)
    {
        viaMachine(_sm);
        var s = _sm.State;
        MainThread.BeginInvokeOnMainThread(() => State = s);
    }

    private static Task OnMain(Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

/// <summary>
/// One entry in the live transcript pane. <paramref name="Source"/> distinguishes user vs assistant
/// for styling; <paramref name="Text"/> is the (possibly partial) utterance and is replaced via
/// <c>with</c>-expression as the agent streams deltas.
/// </summary>
public sealed record TranscriptBubble(TranscriptSource Source, string Text);
