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

        // Download file as binary attachment
        group.MapGet("/download", (string path, AgentEnvironment env) =>
        {
            var dataRoot = Path.GetFullPath(env.DataPath);
            var fullPath = ResolveSafePath(dataRoot, path);
            if (fullPath is null)
                return Results.Forbid();

            if (!System.IO.File.Exists(fullPath))
                return Results.NotFound(new { error = "File not found" });

            var fileName = Path.GetFileName(fullPath);
            return Results.File(fullPath, "application/octet-stream", fileName);
        });

        // Rename a file or directory
        group.MapPost("/rename", (RenameRequest request, AgentEnvironment env) =>
        {
            var dataRoot = Path.GetFullPath(env.DataPath);
            var fullPath = ResolveSafePath(dataRoot, request.Path);
            if (fullPath is null)
                return Results.Forbid();

            // New name must not contain path separators
            if (request.NewName.Contains('/') || request.NewName.Contains('\\'))
                return Results.BadRequest(new { error = "Name cannot contain path separators" });

            var parentDir = Path.GetDirectoryName(fullPath)!;
            var newFullPath = Path.Combine(parentDir, request.NewName);

            // Validate the new path also stays within dataRoot
            if (ResolveSafePath(dataRoot, Path.GetRelativePath(dataRoot, newFullPath)) is null)
                return Results.Forbid();

            var isDirectory = Directory.Exists(fullPath);
            var isFile = System.IO.File.Exists(fullPath);

            if (!isDirectory && !isFile)
                return Results.NotFound(new { error = "File or directory not found" });

            if (Directory.Exists(newFullPath) || System.IO.File.Exists(newFullPath))
                return Results.Conflict(new { error = "A file or directory with that name already exists" });

            if (isDirectory)
                Directory.Move(fullPath, newFullPath);
            else
                System.IO.File.Move(fullPath, newFullPath);

            return Results.Ok(new FileEntry
            {
                Name = request.NewName,
                Path = Path.GetRelativePath(dataRoot, newFullPath).Replace('\\', '/'),
                IsDirectory = isDirectory,
                Size = isFile ? new FileInfo(newFullPath).Length : null,
                ModifiedAt = isDirectory
                    ? new DirectoryInfo(newFullPath).LastWriteTimeUtc
                    : new FileInfo(newFullPath).LastWriteTimeUtc
            });
        });

        // Delete a file or directory
        group.MapDelete("/", (string path, AgentEnvironment env) =>
        {
            var dataRoot = Path.GetFullPath(env.DataPath);
            var fullPath = ResolveSafePath(dataRoot, path);
            if (fullPath is null)
                return Results.Forbid();

            // Prevent deleting the data root itself
            if (string.Equals(fullPath, dataRoot, StringComparison.OrdinalIgnoreCase))
                return Results.Forbid();

            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
            else if (System.IO.File.Exists(fullPath))
                System.IO.File.Delete(fullPath);
            else
                return Results.NotFound(new { error = "File or directory not found" });

            return Results.NoContent();
        });

        // Create a new directory
        group.MapPost("/mkdir", (CreateDirectoryRequest request, AgentEnvironment env) =>
        {
            var dataRoot = Path.GetFullPath(env.DataPath);
            var parentDir = string.IsNullOrEmpty(request.Path) ? dataRoot : ResolveSafePath(dataRoot, request.Path);
            if (parentDir is null)
                return Results.Forbid();

            if (!Directory.Exists(parentDir))
                return Results.NotFound(new { error = "Parent directory not found" });

            // Name must not contain path separators
            if (request.Name.Contains('/') || request.Name.Contains('\\'))
                return Results.BadRequest(new { error = "Name cannot contain path separators" });

            var newDir = Path.Combine(parentDir, request.Name);
            if (ResolveSafePath(dataRoot, Path.GetRelativePath(dataRoot, newDir)) is null)
                return Results.Forbid();

            if (Directory.Exists(newDir))
                return Results.Conflict(new { error = "A directory with that name already exists" });

            Directory.CreateDirectory(newDir);

            var info = new DirectoryInfo(newDir);
            return Results.Ok(new FileEntry
            {
                Name = info.Name,
                Path = Path.GetRelativePath(dataRoot, newDir).Replace('\\', '/'),
                IsDirectory = true,
                Size = null,
                ModifiedAt = info.LastWriteTimeUtc
            });
        });

        // Upload files to the current directory
        group.MapPost("/upload", async (HttpRequest request, AgentEnvironment env) =>
        {
            var dataRoot = Path.GetFullPath(env.DataPath);
            var targetDir = request.Query["path"].FirstOrDefault() ?? "";

            var fullDir = ResolveSafePath(dataRoot, targetDir);
            if (fullDir is null)
                return Results.Forbid();

            if (!Directory.Exists(fullDir))
                return Results.NotFound(new { error = "Directory not found" });

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data" });

            var form = await request.ReadFormAsync();
            var uploaded = new List<FileEntry>();

            foreach (var file in form.Files)
            {
                var fileName = Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var filePath = Path.Combine(fullDir, fileName);

                // Validate the target path stays within dataRoot
                if (ResolveSafePath(dataRoot, Path.GetRelativePath(dataRoot, filePath)) is null)
                    continue;

                await using var stream = new FileStream(filePath, FileMode.Create);
                await file.CopyToAsync(stream);

                var info = new FileInfo(filePath);
                uploaded.Add(new FileEntry
                {
                    Name = info.Name,
                    Path = Path.GetRelativePath(dataRoot, filePath).Replace('\\', '/'),
                    IsDirectory = false,
                    Size = info.Length,
                    ModifiedAt = info.LastWriteTimeUtc
                });
            }

            return Results.Ok(uploaded);
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
/// Request to create a new directory.
/// </summary>
public sealed class CreateDirectoryRequest
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// Request to rename a file or directory.
/// </summary>
public sealed class RenameRequest
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("newName")]
    public required string NewName { get; init; }
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
