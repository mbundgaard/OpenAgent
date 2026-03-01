namespace OpenAgent.Models.Voice;

public abstract record VoiceEvent;

public sealed record SpeechStarted : VoiceEvent;
public sealed record SpeechStopped : VoiceEvent;
public sealed record AudioDelta(ReadOnlyMemory<byte> Audio) : VoiceEvent;
public sealed record AudioDone : VoiceEvent;
public sealed record TranscriptDelta(string Text, TranscriptSource Source) : VoiceEvent;
public sealed record TranscriptDone(string Text, TranscriptSource Source) : VoiceEvent;
public sealed record SessionError(string Message) : VoiceEvent;

public enum TranscriptSource { User, Assistant }
