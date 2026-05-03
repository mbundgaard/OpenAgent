namespace OpenAgent.App.Core.Services;

/// <summary>Manages multiple named server connections. iOS uses Keychain; tests use in-memory.</summary>
public interface IConnectionStore
{
    /// <summary>Returns all stored connections.</summary>
    Task<List<ServerConnection>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Returns the currently active connection, or null if none exist.</summary>
    Task<ServerConnection?> LoadActiveAsync(CancellationToken ct = default);

    /// <summary>Adds or updates a connection by its Id.</summary>
    Task SaveAsync(ServerConnection connection, CancellationToken ct = default);

    /// <summary>Removes a connection. Returns the number of connections remaining.</summary>
    Task<int> DeleteAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Sets the active connection by Id.</summary>
    Task SetActiveAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Returns the active connection Id, or null if none set.</summary>
    Task<string?> GetActiveIdAsync(CancellationToken ct = default);
}
