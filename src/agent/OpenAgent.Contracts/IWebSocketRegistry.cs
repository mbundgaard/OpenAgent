using System.Net.WebSockets;

namespace OpenAgent.Contracts;

/// <summary>
/// Tracks active WebSocket connections by conversation ID so that server-initiated
/// messages (e.g. scheduled task output) can be pushed to connected web app clients.
/// </summary>
public interface IWebSocketRegistry
{
    /// <summary>Registers an open WebSocket for a conversation.</summary>
    void Register(string conversationId, WebSocket webSocket);

    /// <summary>Unregisters a WebSocket for a conversation.</summary>
    void Unregister(string conversationId, WebSocket webSocket);

    /// <summary>Returns the active WebSocket for a conversation, or null if none.</summary>
    WebSocket? Get(string conversationId);
}
