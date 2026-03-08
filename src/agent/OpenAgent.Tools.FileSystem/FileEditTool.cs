using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Performs a find-and-replace edit on a file. Replaces the first occurrence of old_text with new_text.
/// </summary>
public sealed class FileEditTool(string basePath) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "file_edit",
        Description = "Edit a file by replacing the first occurrence of old_text with new_text. The old_text must exist in the file.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Relative file path to edit" },
                old_text = new { type = "string", description = "Text to find in the file" },
                new_text = new { type = "string", description = "Text to replace it with" }
            },
            required = new[] { "path", "old_text", "new_text" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var path = args.GetProperty("path").GetString()
            ?? throw new ArgumentException("path is required");
        var oldText = args.GetProperty("old_text").GetString()
            ?? throw new ArgumentException("old_text is required");
        var newText = args.GetProperty("new_text").GetString()
            ?? throw new ArgumentException("new_text is required");

        // Resolve against base path and prevent directory traversal
        var fullPath = Path.GetFullPath(Path.Combine(basePath, path));
        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "path is outside allowed directory" });

        if (!File.Exists(fullPath))
            return JsonSerializer.Serialize(new { error = "file not found", path });

        var content = await File.ReadAllTextAsync(fullPath, ct);

        if (!content.Contains(oldText, StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { error = "old_text not found in file", path });

        // Replace first occurrence only
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        var updated = string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));

        await File.WriteAllTextAsync(fullPath, updated, ct);
        return JsonSerializer.Serialize(new { path, status = "edited" });
    }
}
