using OpenAgent.Contracts;

namespace OpenAgent.Tests.Fakes;

/// <summary>Connection manager that reports nothing running. Lets tests construct a DeliveryRouter.</summary>
public sealed class NoopConnectionManager : IConnectionManager
{
    public bool IsRunning(string connectionId) => false;
    public IChannelProvider? GetProvider(string connectionId) => null;
    public Task StartConnectionAsync(string connectionId, CancellationToken ct) => Task.CompletedTask;
    public Task StopConnectionAsync(string connectionId, CancellationToken ct) => Task.CompletedTask;
    public IEnumerable<(string ConnectionId, IChannelProvider Provider)> GetProviders() => [];
}
