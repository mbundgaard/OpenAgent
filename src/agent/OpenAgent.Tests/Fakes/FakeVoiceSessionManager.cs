using OpenAgent.Contracts;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Inert <see cref="IVoiceSessionManager"/> for tests. Tracks registered sessions in a dictionary
/// so tests can drive the same code path as production (skill tools query this manager to decide
/// whether to deliver the skill body inline as a tool result). When no session has been
/// registered, <see cref="TryGetSession"/> returns false — matching production behavior for
/// text-only conversations.
/// </summary>
public sealed class FakeVoiceSessionManager : IVoiceSessionManager
{
    private readonly Dictionary<string, IVoiceSession> _sessions = new();

    public Task<IVoiceSession> GetOrCreateSessionAsync(string conversationId, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeVoiceSessionManager does not create sessions.");

    public Task CloseSessionAsync(string conversationId)
    {
        _sessions.Remove(conversationId);
        return Task.CompletedTask;
    }

    public bool TryGetSession(string conversationId, out IVoiceSession? session)
    {
        if (_sessions.TryGetValue(conversationId, out var s))
        {
            session = s;
            return true;
        }
        session = null;
        return false;
    }

    public void RegisterSession(string conversationId, IVoiceSession session) =>
        _sessions[conversationId] = session;

    public void UnregisterSession(string conversationId) =>
        _sessions.Remove(conversationId);
}
