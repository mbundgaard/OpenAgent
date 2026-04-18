using System.Linq;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Appends content to the end of a file, creating it if it does not exist.
/// </summary>
public sealed class FileAppendTool(string basePath, Encoding encoding) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "file_append",
        Description = "Append content to the end of a file. Creates the file if it does not exist.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Relative file path to append to" },
                content = new { type = "string", description = "Content to append to the file" }
            },
            required = new[] { "path", "content" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var path = args.GetProperty("path").GetString()
            ?? throw new ArgumentException("path is required");
        var content = args.GetProperty("content").GetString()
            ?? throw new ArgumentException("content is required");

        // Resolve against base path and prevent directory traversal
        var fullPath = Path.GetFullPath(Path.Combine(basePath, path));
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var roots = SymlinkRoots.List(basePath);
            var hint = roots.Count > 0
                ? $"path is outside allowed directory. Configured mount points: {string.Join(", ", roots.Select(r => r + "/"))} — use one of these prefixes for external paths."
                : "path is outside allowed directory";
            return JsonSerializer.Serialize(new { error = hint });
        }

        // Ensure directory exists
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        await File.AppendAllTextAsync(fullPath, content, encoding, ct);
        return JsonSerializer.Serialize(new { path, bytes_appended = content.Length });
    }
}
