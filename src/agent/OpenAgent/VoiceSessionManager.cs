using System.Collections.Concurrent;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent;

public sealed class VoiceSessionManager : IVoiceSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IVoiceSession> _sessions = new();
    private readonly Func<string, ILlmVoiceProvider> _providerFactory;
    private readonly AgentConfig _agentConfig;
    private readonly IConversationStore _store;

    public VoiceSessionManager(Func<string, ILlmVoiceProvider> providerFactory, AgentConfig agentConfig, IConversationStore store)
    {
        _providerFactory = providerFactory;
        _agentConfig = agentConfig;
        _store = store;
    }

    public async Task<IVoiceSession> GetOrCreateSessionAsync(string conversationId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(conversationId, out var existing))
            return existing;

        var conversation = _store.Get(conversationId)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' not found.");

        // Resolve provider from conversation; fall back to current AgentConfig if not set
        var providerKey = string.IsNullOrEmpty(conversation.Provider)
            ? _agentConfig.VoiceProvider
            : conversation.Provider;

        var provider = _providerFactory(providerKey);
        var session = await provider.StartSessionAsync(conversation, ct: ct);

        if (!_sessions.TryAdd(conversationId, session))
        {
            await session.DisposeAsync();
            return _sessions[conversationId];
        }

        return session;
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

    public void RegisterSession(string conversationId, IVoiceSession session)
    {
        // Last-writer-wins: if a stale entry exists (e.g. previous bridge's dispose didn't unregister),
        // overwrite it. The new session is the live one.
        _sessions[conversationId] = session;
    }

    public void UnregisterSession(string conversationId)
    {
        _sessions.TryRemove(conversationId, out _);
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
