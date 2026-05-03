namespace OpenAgent.App.Core.Services;

/// <summary>In-memory connection store for tests. Production iOS uses Keychain.</summary>
public sealed class InMemoryConnectionStore : IConnectionStore
{
    private readonly List<ServerConnection> _connections = new();
    private string? _activeId;

    public Task<List<ServerConnection>> LoadAllAsync(CancellationToken ct = default)
        => Task.FromResult(new List<ServerConnection>(_connections));

    public Task<ServerConnection?> LoadActiveAsync(CancellationToken ct = default)
    {
        if (_connections.Count == 0) return Task.FromResult<ServerConnection?>(null);
        var match = _activeId is not null ? _connections.Find(c => c.Id == _activeId) : null;
        return Task.FromResult<ServerConnection?>(match ?? _connections[0]);
    }

    public Task SaveAsync(ServerConnection connection, CancellationToken ct = default)
    {
        var idx = _connections.FindIndex(c => c.Id == connection.Id);
        if (idx >= 0) _connections[idx] = connection;
        else _connections.Add(connection);
        if (_activeId is null) _activeId = connection.Id;
        return Task.CompletedTask;
    }

    public Task<int> DeleteAsync(string connectionId, CancellationToken ct = default)
    {
        _connections.RemoveAll(c => c.Id == connectionId);
        if (_activeId == connectionId) _activeId = _connections.FirstOrDefault()?.Id;
        return Task.FromResult(_connections.Count);
    }

    public Task SetActiveAsync(string connectionId, CancellationToken ct = default)
    {
        _activeId = connectionId;
        return Task.CompletedTask;
    }

    public Task<string?> GetActiveIdAsync(CancellationToken ct = default)
        => Task.FromResult(_activeId);
}
