using OpenAgent.Models.Voice;

namespace OpenAgent.Contracts;

public interface IVoiceSession : IAsyncDisposable
{
    string SessionId { get; }

    Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default);
    Task CommitAudioAsync(CancellationToken ct = default);

    IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync(CancellationToken ct = default);

    Task CancelResponseAsync(CancellationToken ct = default);
}
