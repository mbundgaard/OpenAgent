using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Log file endpoints — list and read JSONL log files from the data/logs directory.
/// </summary>
public static class LogEndpoints
{
    /// <summary>
    /// Maps log endpoints under /api/logs.
    /// </summary>
    public static void MapLogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/logs").RequireAuthorization();

        // List all log files with metadata
        group.MapGet("/", (AgentEnvironment env) =>
        {
            var logsDir = Path.Combine(env.DataPath, "logs");
            if (!Directory.Exists(logsDir))
                return Results.Ok(Array.Empty<LogFileInfo>());

            var files = Directory.GetFiles(logsDir, "*.jsonl")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Name)
                .Select(f =>
                {
                    var lineCount = CountLines(f.FullName);
                    return new LogFileInfo
                    {
                        Filename = f.Name,
                        Date = ExtractDate(f.Name),
                        SizeBytes = f.Length,
                        LineCount = lineCount
                    };
                })
                .ToList();

            return Results.Ok(files);
        });

        // Read lines from a log file with paging
        group.MapGet("/{filename}", (string filename, int? offset, int? limit, AgentEnvironment env) =>
        {
            var logsDir = Path.Combine(env.DataPath, "logs");
            var filePath = Path.Combine(logsDir, filename);

            // Prevent directory traversal
            if (!Path.GetFullPath(filePath).StartsWith(Path.GetFullPath(logsDir)))
                return Results.Forbid();

            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Log file not found" });

            var actualOffset = offset ?? 0;
            var actualLimit = limit ?? 200;

            // Read with shared access (Serilog holds a write lock on today's file)
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var allLines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim().Length > 0)
                    allLines.Add(line);
            }

            var totalLines = allLines.Count;
            var pageLines = allLines.Skip(actualOffset).Take(actualLimit).ToList();

            return Results.Ok(new LogPageResponse
            {
                Filename = filename,
                TotalLines = totalLines,
                Offset = actualOffset,
                Limit = actualLimit,
                Lines = pageLines
            });
        });
    }

    private static int CountLines(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var count = 0;
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim().Length > 0) count++;
            }
            return count;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static string? ExtractDate(string filename)
    {
        // Extract date from "log-20260408.jsonl" pattern
        var name = Path.GetFileNameWithoutExtension(filename);
        var dash = name.IndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : null;
    }
}

/// <summary>
/// Metadata about a log file.
/// </summary>
public sealed class LogFileInfo
{
    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("date")]
    public string? Date { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("lineCount")]
    public int LineCount { get; init; }
}

/// <summary>
/// A page of log lines from a file.
/// </summary>
public sealed class LogPageResponse
{
    [JsonPropertyName("filename")]
    public required string Filename { get; init; }

    [JsonPropertyName("totalLines")]
    public int TotalLines { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("lines")]
    public required List<string> Lines { get; init; }
}
