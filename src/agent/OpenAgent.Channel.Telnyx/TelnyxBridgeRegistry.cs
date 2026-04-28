using System.Collections.Concurrent;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Per-application registry of active media bridges with parallel indices on
/// <c>call_control_id</c> (used by the DTMF webhook to dispatch digits) and
/// <c>conversation_id</c> (used by <see cref="EndCallTool"/> to find the bridge for the current
/// LLM turn). Conversation id can change mid-call when DTMF triggers a swap to an extension
/// conversation — call <see cref="UpdateConversationId"/> to re-key without unregistering.
/// Bridges register at start of <c>RunAsync</c> and unregister in the finally block.
/// </summary>
public sealed class TelnyxBridgeRegistry
{
    private readonly ConcurrentDictionary<string, object> _byConversation = new();
    private readonly ConcurrentDictionary<string, object> _byCallControl = new();

    public void Register(string callControlId, string conversationId, object bridge)
    {
        _byCallControl[callControlId] = bridge;
        _byConversation[conversationId] = bridge;
    }

    public void Unregister(string callControlId, string conversationId)
    {
        _byCallControl.TryRemove(callControlId, out _);
        _byConversation.TryRemove(conversationId, out _);
    }

    /// <summary>
    /// Re-key the conversation index after a mid-call swap (DTMF extension routing). The
    /// call-control-id index is unchanged. If the call control id is unknown, the call is a no-op.
    /// </summary>
    public void UpdateConversationId(string callControlId, string oldConversationId, string newConversationId)
    {
        if (_byCallControl.TryGetValue(callControlId, out var bridge))
        {
            _byConversation.TryRemove(oldConversationId, out _);
            _byConversation[newConversationId] = bridge;
        }
    }

    public bool TryGet(string conversationId, out object? bridge)
        => _byConversation.TryGetValue(conversationId, out bridge);

    public bool TryGetByCallControlId(string callControlId, out object? bridge)
        => _byCallControl.TryGetValue(callControlId, out bridge);
}
