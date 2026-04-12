namespace OpenAgent.LlmVoice.GrokRealtime.Protocol;

/// <summary>
/// String constants for Grok Realtime API event types.
/// Mostly identical to OpenAI Realtime spec; audio event names differ slightly.
/// </summary>
internal static class EventTypes
{
    // Client → Server
    public const string SessionUpdate = "session.update";
    public const string InputAudioBufferAppend = "input_audio_buffer.append";
    public const string InputAudioBufferCommit = "input_audio_buffer.commit";
    public const string ResponseCancel = "response.cancel";
    public const string ConversationItemCreate = "conversation.item.create";
    public const string ResponseCreate = "response.create";

    // Server → Client
    public const string SessionCreated = "session.created";
    public const string SpeechStarted = "input_audio_buffer.speech_started";
    public const string SpeechStopped = "input_audio_buffer.speech_stopped";

    // Grok uses response.output_audio.* (Azure uses response.audio.*)
    public const string ResponseAudioDelta = "response.output_audio.delta";
    public const string ResponseAudioDone = "response.output_audio.done";

    // Grok uses response.output_audio_transcript.* (Azure uses response.audio_transcript.*)
    public const string ResponseAudioTranscriptDelta = "response.output_audio_transcript.delta";
    public const string ResponseAudioTranscriptDone = "response.output_audio_transcript.done";

    public const string InputAudioTranscriptionCompleted = "conversation.item.input_audio_transcription.completed";
    public const string InputAudioTranscriptionDelta = "conversation.item.input_audio_transcription.delta";
    public const string FunctionCallArgumentsDone = "response.function_call_arguments.done";
    public const string Error = "error";
}
