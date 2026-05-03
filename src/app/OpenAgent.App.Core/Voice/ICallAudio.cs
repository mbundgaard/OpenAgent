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

    /// <summary>Starts looping a thinking sound (e.g. during tool calls). No-op if already playing.</summary>
    void PlayThinkingLoop();

    /// <summary>Stops the thinking sound loop. Safe to call when not playing.</summary>
    void StopThinkingLoop();

    event Action<byte[]>? OnPcmCaptured;
}
