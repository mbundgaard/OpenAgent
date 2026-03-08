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
        var workspace = Path.GetFullPath(Path.Combine(environment.DataPath, "workspace"));
        Directory.CreateDirectory(workspace);
        Tools = [new FileReadTool(workspace), new FileWriteTool(workspace), new FileAppendTool(workspace), new FileEditTool(workspace)];
    }
}
