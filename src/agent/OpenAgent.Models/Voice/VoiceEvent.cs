namespace OpenAgent.Models.Voice;

/// <summary>
/// Base type for all events emitted by a voice session — audio chunks, transcripts,
/// speech detection signals, and errors.
/// </summary>
public abstract record VoiceEvent;

/// <summary>
/// Emitted once per session, before audio flows. Carries the negotiated audio format so
/// the client can configure its AudioContext / microphone to match the provider.
/// Codec values: "pcm16", "g711_ulaw", "g711_alaw".
/// </summary>
public sealed record SessionReady(
    int InputSampleRate,
    int OutputSampleRate,
    string InputCodec,
    string OutputCodec) : VoiceEvent;

public sealed record SpeechStarted : VoiceEvent;
public sealed record SpeechStopped : VoiceEvent;
public sealed record AudioDelta(ReadOnlyMemory<byte> Audio) : VoiceEvent;
public sealed record AudioDone : VoiceEvent;
public sealed record TranscriptDelta(string Text, TranscriptSource Source) : VoiceEvent;
public sealed record TranscriptDone(string Text, TranscriptSource Source) : VoiceEvent;
public sealed record SessionError(string Message) : VoiceEvent;

/// <summary>
/// Emitted when the session is about to execute a tool call. Endpoints translate this
/// into a "thinking_started" cue so callers can play a placeholder while the tool runs.
/// </summary>
public sealed record VoiceToolCallStarted(string Name, string CallId) : VoiceEvent;

/// <summary>
/// Emitted once a tool call has completed (success or error). Endpoints translate this
/// into a "thinking_stopped" cue.
/// </summary>
public sealed record VoiceToolCallCompleted(string CallId, string Result) : VoiceEvent;

public enum TranscriptSource { User, Assistant }
