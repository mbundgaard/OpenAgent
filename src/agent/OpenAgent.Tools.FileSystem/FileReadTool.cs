using System.Text;
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Reads the contents of a file at a given path. Supports offset/limit pagination
/// and returns numbered lines for easy reference.
/// </summary>
public sealed class FileReadTool(string basePath, Encoding encoding, int maxFileSize = 1_048_576, int defaultMaxLines = 2000) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "file_read",
        Description = "Read the contents of a file. Returns numbered lines. Use offset and limit to paginate large files.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Relative file path to read" },
                offset = new { type = "integer", description = "Line number to start from (1-based, default 1)" },
                limit = new { type = "integer", description = "Maximum number of lines to return (default 2000)" }
            },
            required = new[] { "path" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
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

        // Guard against huge files
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > maxFileSize)
            return JsonSerializer.Serialize(new { error = $"file too large: {fileInfo.Length} bytes (max {maxFileSize})", path });

        // Parse optional pagination parameters
        var offset = args.TryGetProperty("offset", out var offsetEl) && offsetEl.ValueKind == JsonValueKind.Number
            ? Math.Max(1, offsetEl.GetInt32())
            : 1;
        var limit = args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number
            ? Math.Max(1, limitEl.GetInt32())
            : defaultMaxLines;

        var allLines = await File.ReadAllLinesAsync(fullPath, encoding, ct);
        var totalLines = allLines.Length;

        // Slice to requested range (offset is 1-based)
        var startIndex = offset - 1;
        var selected = allLines.Skip(startIndex).Take(limit).ToArray();

        // Format with line numbers
        var numbered = selected
            .Select((line, i) => $"{startIndex + i + 1}: {line}");
        var content = string.Join("\n", numbered);

        // Indicate if there are more lines
        var remaining = totalLines - startIndex - selected.Length;
        if (remaining > 0)
            content += $"\n... ({remaining} more lines)";

        return JsonSerializer.Serialize(new { path, total_lines = totalLines, content });
    }
}
