using OpenAgent.Models.Voice;

namespace OpenAgent.Contracts;

public interface ILlmVoiceProvider
{
    Task<IVoiceSession> StartSessionAsync(
        VoiceSessionConfig config, CancellationToken ct = default);
}
