using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;

namespace OpenAgent.ConfigStore.File;

/// <summary>
/// Persists connections as a JSON array in {dataPath}/config/connections.json.
/// </summary>
public sealed class FileConnectionStore : IConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<FileConnectionStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileConnectionStore(AgentEnvironment environment, ILogger<FileConnectionStore> logger)
    {
        var directory = Path.Combine(environment.DataPath, "config");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "connections.json");
        _logger = logger;
    }

    /// <summary>Returns all connections from disk.</summary>
    public List<Connection> LoadAll()
    {
        _lock.Wait();
        try
        {
            return ReadFile();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Returns a single connection by ID, or null if not found.</summary>
    public Connection? Load(string connectionId)
    {
        _lock.Wait();
        try
        {
            return ReadFile().FirstOrDefault(c => c.Id == connectionId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates or updates a connection and writes to disk.</summary>
    public void Save(Connection connection)
    {
        _lock.Wait();
        try
        {
            var connections = ReadFile();
            var index = connections.FindIndex(c => c.Id == connection.Id);

            if (index >= 0)
                connections[index] = connection;
            else
                connections.Add(connection);

            WriteFile(connections);
            _logger.LogInformation("Saved connection {ConnectionId}", connection.Id);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Deletes a connection by ID.</summary>
    public void Delete(string connectionId)
    {
        _lock.Wait();
        try
        {
            var connections = ReadFile();
            var removed = connections.RemoveAll(c => c.Id == connectionId);

            if (removed > 0)
            {
                WriteFile(connections);
                _logger.LogInformation("Deleted connection {ConnectionId}", connectionId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private List<Connection> ReadFile()
    {
        if (!System.IO.File.Exists(_path))
            return [];

        var bytes = System.IO.File.ReadAllBytes(_path);
        return JsonSerializer.Deserialize<List<Connection>>(bytes) ?? [];
    }

    private void WriteFile(List<Connection> connections)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(connections, JsonOptions);
        System.IO.File.WriteAllBytes(_path, bytes);
    }
}
