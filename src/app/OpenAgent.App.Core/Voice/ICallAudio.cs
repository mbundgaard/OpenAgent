namespace OpenAgent.App.Core.Voice;

/// <summary>Platform-specific audio capture + playback for an active call. iOS impl uses AVAudioEngine.</summary>
public interface ICallAudio : IAsyncDisposable
{
    Task StartAsync(int sampleRate, CancellationToken ct);
    Task StopAsync();
    void EnqueuePlayback(byte[] pcm16);
    void FlushPlayback();
    void SetMuted(bool muted);

    /// <summary>
    /// Routes playback to the loud speaker when <c>true</c>, or back to the default route
    /// (earpiece in voice-chat sessions) when <c>false</c>. Toggleable mid-call.
    /// </summary>
    void SetSpeakerOutput(bool useSpeaker);

    event Action<byte[]>? OnPcmCaptured;
}
