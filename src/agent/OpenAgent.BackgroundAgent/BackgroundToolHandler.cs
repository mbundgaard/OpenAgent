using OpenAgent.Contracts;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// Groups the background-agent tools (<see cref="PostToMainTool"/>) under one capability
/// domain so <c>AgentLogic</c> picks them up. Today there is only one tool; the handler
/// shape keeps registration consistent with other tool families.
/// </summary>
public sealed class BackgroundToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public BackgroundToolHandler(PostToMainTool postToMain)
    {
        Tools = [postToMain];
    }
}
