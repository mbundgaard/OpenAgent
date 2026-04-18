using System.Linq;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Writes content to a file, creating it if it does not exist or overwriting if it does.
/// Guards against writing excessively large content.
/// </summary>
public sealed class FileWriteTool(string basePath, Encoding encoding, int maxFileSize = 1_048_576) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "file_write",
        Description = "Write content to a file. Creates the file if it does not exist, overwrites if it does.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Relative file path to write" },
                content = new { type = "string", description = "Content to write to the file" }
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

        // Guard against writing excessively large content
        if (content.Length > maxFileSize)
            return JsonSerializer.Serialize(new { error = $"content too large: {content.Length} bytes (max {maxFileSize})", path });

        // Ensure directory exists
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(fullPath, content, encoding, ct);
        return JsonSerializer.Serialize(new { path, bytes_written = content.Length });
    }
}
