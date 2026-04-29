using System.Threading.Channels;
using OpenAgent.Contracts;
using OpenAgent.Models.Voice;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IVoiceSession"/> for tests. Captures audio sent into the session,
/// flags Commit/Cancel calls, and exposes an unbounded <see cref="Channel{T}"/> so tests can
/// emit <see cref="VoiceEvent"/>s out of the session at will.
/// </summary>
public sealed class FakeVoiceSession : IVoiceSession
{
    public string SessionId => "fake-session";
    public List<byte[]> SentAudio { get; } = new();
    public bool CommitCalled;
    public bool CancelCalled;
    public bool DisposeCalled;
    private readonly Channel<VoiceEvent> _events =
        System.Threading.Channels.Channel.CreateUnbounded<VoiceEvent>();

    public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
    {
        lock (SentAudio) SentAudio.Add(audio.ToArray());
        return Task.CompletedTask;
    }

    public Task CommitAudioAsync(CancellationToken ct = default)
    {
        CommitCalled = true;
        return Task.CompletedTask;
    }

    public Task CancelResponseAsync(CancellationToken ct = default)
    {
        CancelCalled = true;
        return Task.CompletedTask;
    }

    public List<string> InjectedUserMessages { get; } = new();
    public int RequestResponseCount;

    public Task AddUserMessageAsync(string text, CancellationToken ct = default)
    {
        InjectedUserMessages.Add(text);
        return Task.CompletedTask;
    }

    public Task RequestResponseAsync(CancellationToken ct = default)
    {
        RequestResponseCount++;
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync(CancellationToken ct = default) =>
        _events.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync()
    {
        DisposeCalled = true;
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void Emit(VoiceEvent evt) => _events.Writer.TryWrite(evt);
    public void EndSession() => _events.Writer.TryComplete();
}
