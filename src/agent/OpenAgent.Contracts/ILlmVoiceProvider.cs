using OpenAgent.Models.Voice;

namespace OpenAgent.Contracts;

public interface ILlmVoiceProvider: IConfigurable
{
    Task<IVoiceSession> StartSessionAsync(VoiceSessionOptions config, CancellationToken ct = default);
}

public interface IVoiceSession : IAsyncDisposable
{
    string SessionId { get; }

    Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default);
    Task CommitAudioAsync(CancellationToken ct = default);

    IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync(CancellationToken ct = default);

    Task CancelResponseAsync(CancellationToken ct = default);
}