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

            // Resolve and validate the target directory stays within dataRoot
            var fullPath = Path.GetFullPath(Path.Combine(dataRoot, requestedPath));
            if (!fullPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
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
