using System.Collections.Concurrent;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent;

public sealed class VoiceSessionManager : IVoiceSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IVoiceSession> _sessions = new();
    private readonly ILlmVoiceProvider _voiceProvider;
    private readonly IConversationStore _store;

    public VoiceSessionManager(ILlmVoiceProvider voiceProvider, IConversationStore store)
    {
        _voiceProvider = voiceProvider;
        _store = store;
    }

    public async Task<IVoiceSession> GetOrCreateSessionAsync(string conversationId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(conversationId, out var existing))
            return existing;

        var conversation = _store.Get(conversationId)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' not found.");

        var session = await _voiceProvider.StartSessionAsync(conversation, ct);

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

        // Mark voice session as closed on the conversation
        var conversation = _store.Get(conversationId);
        if (conversation is not null)
        {
            conversation.VoiceSessionOpen = false;
            _store.Update(conversation);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, session) in _sessions)
            await session.DisposeAsync();

        _sessions.Clear();
    }
}
