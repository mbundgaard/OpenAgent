namespace OpenAgent.Contracts;

/// <summary>
/// Manages running channel provider instances. Used by endpoints to query
/// connection status and retrieve providers for webhook routing.
/// </summary>
public interface IConnectionManager
{
    /// <summary>Returns whether a connection is currently running.</summary>
    bool IsRunning(string connectionId);

    /// <summary>Returns the running provider for a connection, or null if not running.</summary>
    IChannelProvider? GetProvider(string connectionId);

    /// <summary>Starts a connection by ID.</summary>
    Task StartConnectionAsync(string connectionId, CancellationToken ct);

    /// <summary>Stops a running connection by ID.</summary>
    Task StopConnectionAsync(string connectionId, CancellationToken ct);

    /// <summary>Returns all running providers.</summary>
    IEnumerable<(string ConnectionId, IChannelProvider Provider)> GetProviders();
}
