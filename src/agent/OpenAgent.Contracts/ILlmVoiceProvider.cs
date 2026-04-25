using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;

namespace OpenAgent.Contracts;

/// <summary>
/// Factory for creating real-time voice sessions against an LLM backend (e.g. Azure OpenAI Realtime).
/// </summary>
public interface ILlmVoiceProvider: IConfigurable
{
    /// <summary>Opens a new bidirectional voice session with the configured backend.</summary>
    /// <param name="conversation">Owning conversation; the session inherits its system prompt and history.</param>
    /// <param name="options">Per-session codec/rate. When null the provider uses its default (pcm16 24 kHz on Azure/Grok).</param>
    Task<IVoiceSession> StartSessionAsync(
        Conversation conversation,
        VoiceSessionOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>
/// A live bidirectional voice session — accepts streamed audio input and emits voice events
/// (audio deltas, transcripts, errors) as they arrive from the LLM.
/// </summary>
public interface IVoiceSession : IAsyncDisposable
{
    /// <summary>Server-assigned session identifier.</summary>
    string SessionId { get; }

    /// <summary>Streams a chunk of PCM audio to the session input buffer.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default);

    /// <summary>Signals that the current audio input is complete and ready for processing.</summary>
    Task CommitAudioAsync(CancellationToken ct = default);

    /// <summary>Yields voice events (audio, transcripts, errors) as they arrive from the server.</summary>
    IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync(CancellationToken ct = default);

    /// <summary>Interrupts the current assistant response.</summary>
    Task CancelResponseAsync(CancellationToken ct = default);
}