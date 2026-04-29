namespace OpenAgent.App.Core.Voice;

/// <summary>One chunk emitted by the receive loop — either a typed event or a raw audio buffer.</summary>
public abstract record VoiceFrame
{
    public sealed record EventFrame(VoiceEvent Event) : VoiceFrame;
    public sealed record AudioFrame(byte[] Pcm16) : VoiceFrame;
    public sealed record Disconnected(string? Reason, bool AuthRejected) : VoiceFrame;
}
