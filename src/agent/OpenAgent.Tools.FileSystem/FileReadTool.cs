using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Reads the contents of a file at a given path.
/// </summary>
public sealed class FileReadTool(string basePath) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "file_read",
        Description = "Read the contents of a file. Returns the file content as text.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Relative file path to read" }
            },
            required = new[] { "path" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var path = args.GetProperty("path").GetString()
            ?? throw new ArgumentException("path is required");

        // Resolve against base path and prevent directory traversal
        var fullPath = Path.GetFullPath(Path.Combine(basePath, path));
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "path is outside allowed directory" });

        if (!File.Exists(fullPath))
            return JsonSerializer.Serialize(new { error = "file not found", path });

        var content = await File.ReadAllTextAsync(fullPath, ct);
        return JsonSerializer.Serialize(new { path, content });
    }
}
