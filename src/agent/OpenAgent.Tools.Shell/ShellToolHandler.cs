using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Shell;

/// <summary>
/// Groups shell tools under a single handler.
/// All operations are scoped to the data directory.
/// </summary>
public sealed class ShellToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public ShellToolHandler(AgentEnvironment environment, ILoggerFactory loggerFactory)
    {
        var dataPath = Path.GetFullPath(environment.DataPath);
        Directory.CreateDirectory(dataPath);
        Tools = [new ShellExecTool(dataPath, loggerFactory.CreateLogger<ShellExecTool>())];
    }
}
