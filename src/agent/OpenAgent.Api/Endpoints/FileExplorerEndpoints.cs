using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// File explorer endpoints — browse directory contents within the data directory.
/// All paths are resolved relative to the configured data root.
/// </summary>
public static class FileExplorerEndpoints
{
    /// <summary>
    /// Maps file browsing endpoints under /api/files.
    /// </summary>
    public static void MapFileExplorerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/files").RequireAuthorization();

        // List directory contents
        group.MapGet("/", (string? path, AgentEnvironment env) =>
        {
            var dataRoot = Path.GetFullPath(env.DataPath);
            var requestedPath = string.IsNullOrEmpty(path) ? "" : path;

            var fullPath = ResolveSafePath(dataRoot, requestedPath);
            if (fullPath is null)
                return Results.Forbid();

            if (!Directory.Exists(fullPath))
                return Results.NotFound(new { error = "Directory not found" });

            var entries = new List<FileEntry>();

            // Directories first
            foreach (var dir in Directory.GetDirectories(fullPath).Order())
            {
                var info = new DirectoryInfo(dir);
                entries.Add(new FileEntry
                {
                    Name = info.Name,
                    Path = Path.GetRelativePath(dataRoot, dir).Replace('\\', '/'),
                    IsDirectory = true,
                    Size = null,
                    ModifiedAt = info.LastWriteTimeUtc
                });
            }

            // Then files
            foreach (var file in Directory.GetFiles(fullPath).Order())
            {
                var info = new FileInfo(file);
                entries.Add(new FileEntry
                {
                    Name = info.Name,
                    Path = Path.GetRelativePath(dataRoot, file).Replace('\\', '/'),
                    IsDirectory = false,
                    Size = info.Length,
                    ModifiedAt = info.LastWriteTimeUtc
                });
            }

            return Results.Ok(entries);
        });

        // Read file contents as text
        group.MapGet("/content", (string path, AgentEnvironment env) =>
        {
            var dataRoot = Path.GetFullPath(env.DataPath);
            var fullPath = ResolveSafePath(dataRoot, path);
            if (fullPath is null)
                return Results.Forbid();

            if (!System.IO.File.Exists(fullPath))
                return Results.NotFound(new { error = "File not found" });

            var content = System.IO.File.ReadAllText(fullPath);
            return Results.Ok(new FileContentResponse
            {
                Path = Path.GetRelativePath(dataRoot, fullPath).Replace('\\', '/'),
                Name = Path.GetFileName(fullPath),
                Content = content
            });
        });
    }

    /// <summary>
    /// Resolves a relative path within the data root and validates it doesn't escape.
    /// Returns null if the path escapes the root.
    /// </summary>
    private static string? ResolveSafePath(string dataRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(dataRoot, relativePath));
        return fullPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }
}

/// <summary>
/// Represents a file or directory entry in the explorer listing.
/// </summary>
public sealed class FileEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("isDirectory")]
    public required bool IsDirectory { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("modifiedAt")]
    public DateTime ModifiedAt { get; init; }
}

/// <summary>
/// Response containing a file's text content.
/// </summary>
public sealed class FileContentResponse
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}
