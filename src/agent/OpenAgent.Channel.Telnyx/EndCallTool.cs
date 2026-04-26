using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Tool the agent calls to politely end a phone call. Sets pendingHangup on the active bridge,
/// which waits for the farewell audio to finish (per the bridge's hangup state machine) before
/// issuing the actual Telnyx hangup. Only effective during a live phone call — the registry
/// only holds bridges for active Telnyx calls, so chat/voice conversations get "no active call".
/// </summary>
public sealed class EndCallTool : ITool
{
    private readonly TelnyxBridgeRegistry _registry;

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

    public EndCallTool(TelnyxBridgeRegistry registry)
    {
        _registry = registry;
    }

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        // The bridge registry is the source of truth for "is this a live phone call?".
        // No bridge → not a phone call (or already torn down by caller hangup).
        if (!_registry.TryGet(conversationId, out var bridge) || bridge is not ITelnyxBridge typed)
            return Task.FromResult("error: no active call to end");

        // Poke pendingHangup; the bridge drains farewell audio, then hangs up Telnyx-side.
        typed.SetPendingHangup();
        return Task.FromResult("ok");
    }
}
