using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Writes content to a file, creating it if it does not exist or overwriting if it does.
/// Guards against writing excessively large content.
/// </summary>
public sealed class FileWriteTool(string basePath, int maxFileSize = 1_048_576) : ITool
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

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var path = args.GetProperty("path").GetString()
            ?? throw new ArgumentException("path is required");
        var content = args.GetProperty("content").GetString()
            ?? throw new ArgumentException("content is required");

        // Resolve against base path and prevent directory traversal
        var fullPath = Path.GetFullPath(Path.Combine(basePath, path));
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "path is outside allowed directory" });

        // Guard against writing excessively large content
        if (content.Length > maxFileSize)
            return JsonSerializer.Serialize(new { error = $"content too large: {content.Length} bytes (max {maxFileSize})", path });

        // Ensure directory exists
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(fullPath, content, ct);
        return JsonSerializer.Serialize(new { path, bytes_written = content.Length });
    }
}
