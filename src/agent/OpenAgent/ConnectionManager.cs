using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent;

/// <summary>
/// Manages channel provider instances for each connection.
/// Starts enabled connections on startup, supports runtime start/stop.
/// </summary>
public sealed class ConnectionManager : IConnectionManager, IHostedService
{
    private readonly IConnectionStore _connectionStore;
    private readonly IEnumerable<IChannelProviderFactory> _factories;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, IChannelProvider> _running = new();

    public ConnectionManager(
        IConnectionStore connectionStore,
        IEnumerable<IChannelProviderFactory> factories,
        ILogger<ConnectionManager> logger)
    {
        _connectionStore = connectionStore;
        _factories = factories;
        _logger = logger;
    }

    /// <summary>Starts all enabled connections on application startup.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var connections = _connectionStore.LoadAll();
        var enabled = connections.Where(c => c.Enabled).ToList();

        _logger.LogInformation("ConnectionManager starting {Count} enabled connection(s)", enabled.Count);

        foreach (var connection in enabled)
        {
            try
            {
                await StartConnectionAsync(connection.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start connection {ConnectionId}", connection.Id);
            }
        }
    }

    /// <summary>Stops all running connections on application shutdown.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("ConnectionManager stopping {Count} running connection(s)", _running.Count);

        foreach (var (connectionId, provider) in _running)
        {
            try
            {
                await provider.StopAsync(ct);
                _logger.LogInformation("Stopped connection {ConnectionId}", connectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping connection {ConnectionId}", connectionId);
            }
        }

        _running.Clear();
    }

    /// <summary>Starts a connection by ID. Creates the provider and calls StartAsync.</summary>
    public async Task StartConnectionAsync(string connectionId, CancellationToken ct)
    {
        if (_running.ContainsKey(connectionId))
        {
            _logger.LogDebug("Connection {ConnectionId} is already running", connectionId);
            return;
        }

        var connection = _connectionStore.Load(connectionId)
            ?? throw new InvalidOperationException($"Connection '{connectionId}' not found.");

        var factory = _factories.FirstOrDefault(f =>
            string.Equals(f.Type, connection.Type, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No factory registered for connection type '{connection.Type}'.");

        var provider = factory.Create(connection);
        await provider.StartAsync(ct);

        _running[connectionId] = provider;
        _logger.LogInformation("Started connection {ConnectionId} ({Type})", connectionId, connection.Type);
    }

    /// <summary>Stops a running connection by ID.</summary>
    public async Task StopConnectionAsync(string connectionId, CancellationToken ct)
    {
        if (!_running.TryRemove(connectionId, out var provider))
        {
            _logger.LogDebug("Connection {ConnectionId} is not running", connectionId);
            return;
        }

        await provider.StopAsync(ct);
        _logger.LogInformation("Stopped connection {ConnectionId}", connectionId);
    }

    /// <summary>Returns whether a connection is currently running.</summary>
    public bool IsRunning(string connectionId) => _running.ContainsKey(connectionId);

    /// <summary>Returns the running provider for a connection, or null if not running.</summary>
    public IChannelProvider? GetProvider(string connectionId) =>
        _running.TryGetValue(connectionId, out var provider) ? provider : null;

    /// <summary>Returns all running providers.</summary>
    public IEnumerable<(string ConnectionId, IChannelProvider Provider)> GetProviders() =>
        _running.Select(kv => (kv.Key, kv.Value));
}
