using System.Collections.Concurrent;
using System.Net.WebSockets;
using OpenAgent.Contracts;

namespace OpenAgent;

/// <summary>
/// In-memory registry of active WebSocket connections keyed by conversation ID.
/// Used by DeliveryRouter to push scheduled task output to connected web app clients.
/// Thread-safe — endpoints register/unregister from their async handlers.
/// </summary>
public sealed class WebSocketRegistry : IWebSocketRegistry
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();

    /// <inheritdoc />
    public void Register(string conversationId, WebSocket webSocket)
    {
        _sockets[conversationId] = webSocket;
    }

    /// <inheritdoc />
    public void Unregister(string conversationId, WebSocket webSocket)
    {
        // Only remove if it's the same socket (avoids race with a new connection)
        _sockets.TryRemove(new KeyValuePair<string, WebSocket>(conversationId, webSocket));
    }

    /// <inheritdoc />
    public WebSocket? Get(string conversationId)
    {
        return _sockets.TryGetValue(conversationId, out var ws) && ws.State == WebSocketState.Open
            ? ws
            : null;
    }
}
