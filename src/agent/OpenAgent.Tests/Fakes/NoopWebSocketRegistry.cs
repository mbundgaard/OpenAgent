using System.Net.WebSockets;
using OpenAgent.Contracts;

namespace OpenAgent.Tests.Fakes;

/// <summary>WebSocket registry with no live sockets. Lets tests construct a DeliveryRouter.</summary>
public sealed class NoopWebSocketRegistry : IWebSocketRegistry
{
    public void Register(string conversationId, WebSocket webSocket) { }
    public void Unregister(string conversationId, WebSocket webSocket) { }
    public WebSocket? Get(string conversationId) => null;
}
