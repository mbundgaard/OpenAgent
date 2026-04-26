using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Tool the agent calls to politely end a phone call. Sets pendingHangup on the active bridge,
/// which waits for the farewell audio to finish (per the bridge's hangup state machine) before
/// issuing the actual Telnyx hangup. Phone-only — refuses on text/voice conversations.
/// </summary>
public sealed class EndCallTool : ITool
{
    private readonly TelnyxBridgeRegistry _registry;
    private readonly IConversationStore _store;

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "end_call",
        Description = "Politely end the current phone call. Use only after speaking a brief farewell. The line drops after the farewell finishes playing.",
        Parameters = new
        {
            type = "object",
            properties = new { },
            additionalProperties = false
        }
    };

    public EndCallTool(TelnyxBridgeRegistry registry, IConversationStore store)
    {
        _registry = registry;
        _store = store;
    }

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        // Phone-only: voice/text conversations have no Telnyx bridge to drive the hangup state machine.
        var conv = _store.Get(conversationId);
        if (conv is null || conv.Type != ConversationType.Phone)
            return Task.FromResult("error: end_call only works during a phone call");

        // The bridge may already be torn down (caller hung up) — handle gracefully.
        if (!_registry.TryGet(conversationId, out var bridge) || bridge is not ITelnyxBridge typed)
            return Task.FromResult("error: no active call to end");

        // Poke pendingHangup; the bridge drains farewell audio, then hangs up Telnyx-side.
        typed.SetPendingHangup();
        return Task.FromResult("ok");
    }
}
