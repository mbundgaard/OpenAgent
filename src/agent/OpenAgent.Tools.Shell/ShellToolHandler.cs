using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Shell;

/// <summary>
/// Groups shell tools under a single handler.
/// All operations are scoped to the workspace directory.
/// </summary>
public sealed class ShellToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public ShellToolHandler(AgentEnvironment environment, ILoggerFactory loggerFactory)
    {
        var workspace = Path.GetFullPath(Path.Combine(environment.DataPath, "workspace"));
        Directory.CreateDirectory(workspace);
        Tools = [new ShellExecTool(workspace, loggerFactory.CreateLogger<ShellExecTool>())];
    }
}
