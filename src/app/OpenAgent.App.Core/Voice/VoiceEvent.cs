namespace OpenAgent.App.Core.Voice;

/// <summary>Closed union of voice WebSocket text-frame events.</summary>
public abstract record VoiceEvent
{
    /// <summary>Emitted once per session before audio flows. Carries codec + sample rate negotiation.</summary>
    public sealed record SessionReady(int InputSampleRate, int OutputSampleRate, string InputCodec, string OutputCodec) : VoiceEvent;

    /// <summary>User started speaking (server-side VAD).</summary>
    public sealed record SpeechStarted : VoiceEvent;

    /// <summary>User stopped speaking (server-side VAD).</summary>
    public sealed record SpeechStopped : VoiceEvent;

    /// <summary>Assistant audio response finished playing back from the server.</summary>
    public sealed record AudioDone : VoiceEvent;

    /// <summary>Agent has begun a tool call; client may play a thinking placeholder.</summary>
    public sealed record ThinkingStarted : VoiceEvent;

    /// <summary>Agent's tool call completed; thinking placeholder should stop.</summary>
    public sealed record ThinkingStopped : VoiceEvent;

    /// <summary>Server-side error reported on the voice channel.</summary>
    public sealed record Error(string Message) : VoiceEvent;
}
