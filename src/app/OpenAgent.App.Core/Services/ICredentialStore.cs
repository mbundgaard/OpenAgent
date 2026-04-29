using OpenAgent.App.Core.Onboarding;

namespace OpenAgent.App.Core.Services;

/// <summary>Stores the agent base URL + API token. iOS impl uses Keychain; tests use in-memory.</summary>
public interface ICredentialStore
{
    Task<QrPayload?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(QrPayload payload, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
