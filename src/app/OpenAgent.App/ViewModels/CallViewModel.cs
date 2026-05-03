using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.ViewModels;

/// <summary>
/// Drives the active call: owns the WebSocket session loop, the audio capture/playback pipeline,
/// and the call state machine. A single owning task (<see cref="RunSessionLoopAsync"/>) handles
/// connect → drain → reconnect linearly so flow is never recursive. All UI mutations
/// (<see cref="State"/>, Shell navigation/dialogs) marshal to the main thread via
/// <see cref="MainThread.BeginInvokeOnMainThread"/> or the <c>OnMain</c> helper.
/// </summary>
[QueryProperty(nameof(ConversationId), "conversationId")]
[QueryProperty(nameof(Title), "title")]
public partial class CallViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ICallAudio _audio;
    private readonly ILogger<CallViewModel> _logger;
    private readonly CallStateMachine _sm = new();
    private readonly ReconnectBackoff _backoff = new();

    private IVoiceWebSocketClient? _ws;
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private bool _ended;

    [ObservableProperty] private string? _conversationId;
    [ObservableProperty] private string? _title;
    [ObservableProperty] private CallState _state;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _speakerOn;

    public CallViewModel(IServiceProvider services, ICallAudio audio, ILogger<CallViewModel>? logger = null)
    {
        _services = services;
        _audio = audio;
        _logger = logger ?? NullLogger<CallViewModel>.Instance;
        _audio.OnPcmCaptured += pcm =>
        {
            var ws = _ws;
            var token = _cts?.Token ?? CancellationToken.None;
            if (ws is null || token.IsCancellationRequested) return;
            _ = SendAudioSafe(ws, pcm, token);
        };
    }

    [RelayCommand]
    public Task StartAsync()
    {
        _logger.LogInformation("Call start convo={ConversationId} title={Title}", ConversationId, Title);
        _cts = new CancellationTokenSource();
        _ended = false;
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
                    _logger.LogWarning("Call connect failed: {Error}", ex.Message);
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
                    _logger.LogWarning("Call auth rejected — sending user back to onboarding");
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
                _logger.LogError(ex, "Call session loop crashed: {Error}", ex.Message);
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
                    _logger.LogInformation("SessionReady codecs={InCodec}/{OutCodec} sampleRates={InRate}/{OutRate}",
                        ready.InputCodec, ready.OutputCodec, ready.InputSampleRate, ready.OutputSampleRate);
                    if (!string.Equals(ready.InputCodec, "pcm16", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(ready.OutputCodec, "pcm16", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Codec mismatch — client handles pcm16 only");
                        await OnMain(() => Shell.Current.DisplayAlert("Codec mismatch",
                            $"Agent announced {ready.InputCodec}/{ready.OutputCodec}; this client only handles pcm16.", "OK"));
                        return new VoiceFrame.Disconnected("Unsupported codec", AuthRejected: false);
                    }
                    await _audio.StartAsync(ready.InputSampleRate, ct);
                    SetState(CallState.Listening, viaMachine: sm => sm.Apply(ready));
                    break;

                case VoiceFrame.EventFrame ef:
                    if (ef.Event is VoiceEvent.SpeechStarted) { _audio.StopThinkingLoop(); _audio.FlushPlayback(); }
                    if (ef.Event is VoiceEvent.ThinkingStarted) _audio.PlayThinkingLoop();
                    if (ef.Event is VoiceEvent.ThinkingStopped) _audio.StopThinkingLoop();
                    SetState(_sm.State, viaMachine: sm => sm.Apply(ef.Event));
                    if (ef.Event is VoiceEvent.Error err)
                    {
                        _logger.LogWarning("Voice error from agent: {Error}", err.Message);
                        await OnMain(() => Shell.Current.DisplayAlert("Voice error", err.Message, "OK"));
                    }
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
    public void ToggleSpeaker()
    {
        SpeakerOn = !SpeakerOn;
        _audio.SetSpeakerOutput(SpeakerOn);
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

    private async Task SendAudioSafe(IVoiceWebSocketClient ws, byte[] pcm, CancellationToken ct)
    {
        try { await ws.SendAudioAsync(pcm, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning("Audio send failed: {Error}", ex.Message); }
    }

    private void SetState(CallState newState, Action<CallStateMachine> viaMachine)
    {
        var prev = _sm.State;
        viaMachine(_sm);
        var s = _sm.State;
        if (s != prev) _logger.LogInformation("Call state {From} -> {To}", prev, s);
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
