using OpenAgent.Contracts;
using OpenAgent.Models.Connections;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// In-memory connection store for testing. Stores connections in a dictionary.
/// Pre-seeds with a default connection that has AllowNewConversations = true.
/// </summary>
public class FakeConnectionStore : IConnectionStore
{
    private readonly Dictionary<string, Connection> _connections = new();

    /// <summary>Creates a store with an optional pre-seeded connection.</summary>
    public FakeConnectionStore(string? seedConnectionId = null, bool allowNewConversations = true)
    {
        if (seedConnectionId is not null)
        {
            _connections[seedConnectionId] = new Connection
            {
                Id = seedConnectionId,
                Name = "Test",
                Type = "test",
                Enabled = true,
                AllowNewConversations = allowNewConversations,
                ConversationId = "unused",
            };
        }
    }

    public List<Connection> LoadAll() => [.. _connections.Values];
    public Connection? Load(string connectionId) => _connections.GetValueOrDefault(connectionId);
    public void Save(Connection connection) => _connections[connection.Id] = connection;
    public void Delete(string connectionId) => _connections.Remove(connectionId);
}
