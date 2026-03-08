using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Performs a find-and-replace edit on a file. Requires exactly one match of old_text.
/// Returns a contextual diff showing the change.
/// </summary>
public sealed class FileEditTool(string basePath, int maxFileSize = 1_048_576) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "file_edit",
        Description = "Edit a file by replacing old_text with new_text. The old_text must appear exactly once in the file. Returns a diff showing the change.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Relative file path to edit" },
                old_text = new { type = "string", description = "Text to find in the file (must be unique)" },
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

        // Guard against huge files
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > maxFileSize)
            return JsonSerializer.Serialize(new { error = $"file too large: {fileInfo.Length} bytes (max {maxFileSize})", path });

        var content = await File.ReadAllTextAsync(fullPath, ct);

        // Count occurrences to ensure uniqueness
        var count = CountOccurrences(content, oldText);
        if (count == 0)
            return JsonSerializer.Serialize(new { error = "old_text not found in file", path });
        if (count > 1)
            return JsonSerializer.Serialize(new { error = $"old_text found {count} times, must be unique. Provide more context to make it unique.", path });

        // Replace the single occurrence
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        var updated = string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));

        if (content == updated)
            return JsonSerializer.Serialize(new { error = "no changes would be made, old_text and new_text produce identical content", path });

        await File.WriteAllTextAsync(fullPath, updated, ct);

        // Generate a contextual diff for verification
        var diff = GenerateDiff(content, updated);
        return JsonSerializer.Serialize(new { path, status = "edited", diff });
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string GenerateDiff(string oldContent, string newContent, int contextLines = 4)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        // Find first difference
        var firstDiff = 0;
        while (firstDiff < oldLines.Length && firstDiff < newLines.Length && oldLines[firstDiff] == newLines[firstDiff])
            firstDiff++;

        // Find last difference
        var oldEnd = oldLines.Length - 1;
        var newEnd = newLines.Length - 1;
        while (oldEnd > firstDiff && newEnd > firstDiff && oldLines[oldEnd] == newLines[newEnd])
        {
            oldEnd--;
            newEnd--;
        }

        var lines = new List<string>();
        var contextStart = Math.Max(0, firstDiff - contextLines);

        if (contextStart > 0)
            lines.Add("...");

        // Context before
        for (var i = contextStart; i < firstDiff; i++)
            lines.Add($"  {i + 1}: {oldLines[i]}");

        // Removed lines
        for (var i = firstDiff; i <= oldEnd; i++)
            lines.Add($"- {i + 1}: {oldLines[i]}");

        // Added lines
        for (var i = firstDiff; i <= newEnd; i++)
            lines.Add($"+ {i + 1}: {newLines[i]}");

        // Context after
        var afterEnd = Math.Min(newLines.Length - 1, newEnd + contextLines);
        for (var i = newEnd + 1; i <= afterEnd; i++)
            lines.Add($"  {i + 1}: {newLines[i]}");

        if (afterEnd < newLines.Length - 1)
            lines.Add("...");

        return string.Join("\n", lines);
    }
}
