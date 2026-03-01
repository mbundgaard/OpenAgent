namespace OpenAgent.LlmVoice.OpenAIAzure.Protocol;

/// <summary>
/// String constants for Azure OpenAI Realtime API event types (client-to-server and server-to-client).
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
    public const string ResponseAudioDelta = "response.audio.delta";
    public const string ResponseAudioDone = "response.audio.done";
    public const string InputAudioTranscriptionCompleted = "conversation.item.input_audio_transcription.completed";
    public const string InputAudioTranscriptionDelta = "conversation.item.input_audio_transcription.delta";
    public const string ResponseAudioTranscriptDelta = "response.audio_transcript.delta";
    public const string ResponseAudioTranscriptDone = "response.audio_transcript.done";
    public const string FunctionCallArgumentsDelta = "response.function_call_arguments.delta";
    public const string FunctionCallArgumentsDone = "response.function_call_arguments.done";
    public const string Error = "error";
}
