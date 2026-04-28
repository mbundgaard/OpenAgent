using System.Collections.Concurrent;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Per-application registry of active media bridges, keyed by conversation id. Allows
/// <see cref="EndCallTool"/> to find the bridge for a given conversation without going through
/// the channel provider directly. Bridges register themselves at start of <c>RunAsync</c> and
/// unregister in the finally block.
/// </summary>
public sealed class TelnyxBridgeRegistry
{
    private readonly ConcurrentDictionary<string, object> _bridges = new();

    public void Register(string conversationId, object bridge) => _bridges[conversationId] = bridge;
    public void Unregister(string conversationId) => _bridges.TryRemove(conversationId, out _);
    public bool TryGet(string conversationId, out object? bridge) => _bridges.TryGetValue(conversationId, out bridge);
}
