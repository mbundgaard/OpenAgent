using System.Text;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Groups file system tools (read, write, edit) under a single handler.
/// All operations are scoped to the configured base path.
/// </summary>
public sealed class FileSystemToolHandler : IToolHandler
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public IReadOnlyList<ITool> Tools { get; }

    public FileSystemToolHandler(AgentEnvironment environment)
    {
        var workspace = Path.GetFullPath(environment.DataPath);
        Directory.CreateDirectory(workspace);
        Tools = [
            new FileReadTool(workspace, encoding: Utf8NoBom),
            new FileWriteTool(workspace, encoding: Utf8NoBom),
            new FileAppendTool(workspace, encoding: Utf8NoBom),
            new FileEditTool(workspace, encoding: Utf8NoBom)
        ];
    }
}
