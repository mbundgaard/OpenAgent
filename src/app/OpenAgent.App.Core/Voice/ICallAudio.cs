namespace OpenAgent.App.Core.Voice;

/// <summary>Platform-specific audio capture + playback for an active call. iOS impl uses AVAudioEngine.</summary>
public interface ICallAudio : IAsyncDisposable
{
    Task StartAsync(int sampleRate, CancellationToken ct);
    Task StopAsync();
    void EnqueuePlayback(byte[] pcm16);
    void FlushPlayback();
    void SetMuted(bool muted);
    event Action<byte[]>? OnPcmCaptured;
}
