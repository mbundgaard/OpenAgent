using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telnyx;

/// <summary>Bundles Telnyx-specific tools (currently just <see cref="EndCallTool"/>) for AgentLogic discovery.</summary>
public sealed class TelnyxToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public TelnyxToolHandler(EndCallTool endCall) => Tools = [endCall];
}
