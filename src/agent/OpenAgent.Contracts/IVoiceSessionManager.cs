namespace OpenAgent.Contracts;

public interface IVoiceSessionManager
{
    Task<IVoiceSession> GetOrCreateSessionAsync(string conversationId, CancellationToken ct = default);
    Task CloseSessionAsync(string conversationId);
}
