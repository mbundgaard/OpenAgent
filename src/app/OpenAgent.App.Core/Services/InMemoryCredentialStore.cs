using OpenAgent.App.Core.Onboarding;

namespace OpenAgent.App.Core.Services;

/// <summary>In-memory credential store for tests and development. Production iOS uses Keychain.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private QrPayload? _current;
    public Task<QrPayload?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_current);
    public Task SaveAsync(QrPayload payload, CancellationToken ct = default) { _current = payload; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken ct = default) { _current = null; return Task.CompletedTask; }
}
