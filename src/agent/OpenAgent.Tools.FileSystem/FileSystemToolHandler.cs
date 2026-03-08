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
        var dataPath = Path.GetFullPath(environment.DataPath);
        Directory.CreateDirectory(dataPath);

        // Ensure standard folders exist
        Directory.CreateDirectory(Path.Combine(dataPath, "documents"));
        Directory.CreateDirectory(Path.Combine(dataPath, "projects"));
        Directory.CreateDirectory(Path.Combine(dataPath, "repos"));
        Directory.CreateDirectory(Path.Combine(dataPath, "memory"));
        Tools = [
            new FileReadTool(dataPath, encoding: Utf8NoBom),
            new FileWriteTool(dataPath, encoding: Utf8NoBom),
            new FileAppendTool(dataPath, encoding: Utf8NoBom),
            new FileEditTool(dataPath, encoding: Utf8NoBom)
        ];
    }
}
