namespace OpenAgent.LlmVoice.OpenAI.Protocol;

internal static class EventTypes
{
    // Client → Server
    public const string SessionUpdate = "session.update";
    public const string InputAudioBufferAppend = "input_audio_buffer.append";
    public const string InputAudioBufferCommit = "input_audio_buffer.commit";
    public const string ResponseCancel = "response.cancel";

    // Server → Client
    public const string SessionCreated = "session.created";
    public const string SpeechStarted = "input_audio_buffer.speech_started";
    public const string SpeechStopped = "input_audio_buffer.speech_stopped";
    public const string ResponseAudioDelta = "response.audio.delta";
    public const string ResponseAudioDone = "response.audio.done";
    public const string InputAudioTranscriptionCompleted = "conversation.item.input_audio_transcription.completed";
    public const string InputAudioTranscriptionDelta = "conversation.item.input_audio_transcription.delta";
    public const string ResponseAudioTranscriptDelta = "response.audio_transcript.delta";
    public const string ResponseAudioTranscriptDone = "response.audio_transcript.done";
    public const string Error = "error";
}
