using OpenAgent.Models.Connections;

namespace OpenAgent.Contracts;

/// <summary>
/// Persists and retrieves channel connections.
/// </summary>
public interface IConnectionStore
{
    /// <summary>Returns all connections.</summary>
    List<Connection> LoadAll();

    /// <summary>Returns a single connection by ID, or null if not found.</summary>
    Connection? Load(string connectionId);

    /// <summary>Creates or updates a connection and persists immediately.</summary>
    void Save(Connection connection);

    /// <summary>Deletes a connection by ID.</summary>
    void Delete(string connectionId);
}
