using System.Collections.Concurrent;
using OpenAgent.Contracts;
using OpenAgent.Models.Voice;

namespace OpenAgent;

public sealed class VoiceSessionManager : IVoiceSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IVoiceSession> _sessions = new();
    private readonly ILlmVoiceProvider _voiceProvider;

    public VoiceSessionManager(ILlmVoiceProvider voiceProvider)
    {
        _voiceProvider = voiceProvider;
    }

    public async Task<IVoiceSession> GetOrCreateSessionAsync(string conversationId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(conversationId, out var existing))
            return existing;

        var session = await _voiceProvider.StartSessionAsync(
            new VoiceSessionOptions { ConversationId = conversationId }, ct);

        if (!_sessions.TryAdd(conversationId, session))
        {
            await session.DisposeAsync();
            return _sessions[conversationId];
        }

        return session;
    }

    public async Task CloseSessionAsync(string conversationId)
    {
        if (!_sessions.TryRemove(conversationId, out var session))
            return;

        await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, session) in _sessions)
            await session.DisposeAsync();

        _sessions.Clear();
    }
}
