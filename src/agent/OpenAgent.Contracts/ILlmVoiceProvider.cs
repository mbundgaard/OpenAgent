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

    /// <summary>
    /// Re-sends the session configuration with a freshly built system prompt. Realtime sessions
    /// lock the system prompt at session start, so any later mutation that affects the prompt
    /// (skill activation/deactivation, intention change, mention filter) won't reach the model
    /// without an explicit refresh. No-op when the underlying protocol doesn't support mid-session
    /// updates.
    /// </summary>
    Task RefreshSystemPromptAsync(CancellationToken ct = default);

    /// <summary>
    /// Pushes a synthetic user-role message into the live session and triggers a response.
    /// Used by callers (e.g. the Telnyx bridge on call connect) to make the model speak first
    /// based on a runtime breadcrumb. The message is NOT persisted to conversation history —
    /// it's a one-shot trigger for this session only.
    /// </summary>
    Task SendUserMessageAsync(string text, CancellationToken ct = default);
}