using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Groups file system tools (read, write, edit) under a single handler.
/// All operations are scoped to the configured base path.
/// </summary>
public sealed class FileSystemToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public FileSystemToolHandler(AgentEnvironment environment)
    {
        var basePath = Path.GetFullPath(environment.DataPath);
        Tools = [new FileReadTool(basePath), new FileWriteTool(basePath), new FileEditTool(basePath)];
    }
}
