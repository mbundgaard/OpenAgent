namespace OpenAgent.Contracts;

public interface IVoiceSessionManager
{
    Task<IVoiceSession> GetOrCreateSessionAsync(string conversationId, CancellationToken ct = default);
    Task CloseSessionAsync(string conversationId);

    /// <summary>
    /// Returns the live session for a conversation if one is currently registered. Used by
    /// callers that need to act on an existing session (e.g. system-prompt refresh after a
    /// skill activation) without creating one when none exists.
    /// </summary>
    bool TryGetSession(string conversationId, out IVoiceSession? session);

    /// <summary>
    /// Registers a session not created via <see cref="GetOrCreateSessionAsync"/> — for example,
    /// the Telnyx bridge opens its own session on the call thread and registers it here so
    /// skill tools can find it.
    /// </summary>
    void RegisterSession(string conversationId, IVoiceSession session);

    /// <summary>Removes a registered session without disposing it (the caller owns the lifecycle).</summary>
    void UnregisterSession(string conversationId);
}
