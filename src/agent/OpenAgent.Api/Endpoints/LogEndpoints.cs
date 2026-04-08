using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Log file endpoints — list and read JSONL log files from the data/logs directory.
/// Supports filtering by level, time range, and text search.
/// </summary>
public static class LogEndpoints
{
    // Serilog compact JSON level abbreviations mapped to full names
    private static readonly Dictionary<string, string> LevelAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VRB"] = "Verbose", ["DBG"] = "Debug", ["INF"] = "Information",
        ["WRN"] = "Warning", ["ERR"] = "Error", ["FTL"] = "Fatal",
        ["Verbose"] = "Verbose", ["Debug"] = "Debug", ["Information"] = "Information",
        ["Warning"] = "Warning", ["Error"] = "Error", ["Fatal"] = "Fatal"
    };

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

        // Read lines from a log file with paging and filtering
        // Filters: level (comma-separated), since/until (ISO 8601), search (substring), tail (last N)
        group.MapGet("/{filename}", (
            string filename,
            int? offset,
            int? limit,
            string? level,
            string? since,
            string? until,
            string? search,
            int? tail,
            AgentEnvironment env) =>
        {
            var logsDir = Path.Combine(env.DataPath, "logs");
            var filePath = Path.Combine(logsDir, filename);

            // Prevent directory traversal
            if (!Path.GetFullPath(filePath).StartsWith(Path.GetFullPath(logsDir)))
                return Results.Forbid();

            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Log file not found" });

            // Parse filter parameters
            var levelFilter = ParseLevelFilter(level);
            var sinceFilter = since is not null ? DateTimeOffset.Parse(since) : (DateTimeOffset?)null;
            var untilFilter = until is not null ? DateTimeOffset.Parse(until) : (DateTimeOffset?)null;
            var hasFilters = levelFilter is not null || sinceFilter is not null || untilFilter is not null || search is not null;

            // Read all non-empty lines with shared access
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var allLines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim().Length > 0)
                    allLines.Add(line);
            }

            var totalLines = allLines.Count;

            // Apply filters
            IEnumerable<string> filtered = allLines;
            if (hasFilters)
            {
                filtered = allLines.Where(line => MatchesFilters(line, levelFilter, sinceFilter, untilFilter, search));
            }

            var filteredList = filtered.ToList();
            var matchedLines = filteredList.Count;

            // Apply tail (take last N from filtered results)
            if (tail is > 0)
            {
                var skipCount = Math.Max(0, filteredList.Count - tail.Value);
                filteredList = filteredList.Skip(skipCount).ToList();
            }

            // Apply offset/limit paging
            var actualOffset = offset ?? 0;
            var actualLimit = limit ?? 200;
            var pageLines = filteredList.Skip(actualOffset).Take(actualLimit).ToList();

            return Results.Ok(new LogPageResponse
            {
                Filename = filename,
                TotalLines = totalLines,
                MatchedLines = matchedLines,
                Offset = actualOffset,
                Limit = actualLimit,
                Lines = pageLines
            });
        });
    }

    /// <summary>
    /// Checks if a JSON log line matches all active filters.
    /// </summary>
    private static bool MatchesFilters(
        string line,
        HashSet<string>? levelFilter,
        DateTimeOffset? sinceFilter,
        DateTimeOffset? untilFilter,
        string? search)
    {
        // Text search is a simple substring match on the raw line — fast, no parsing needed
        if (search is not null && !line.Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;

        // Level and time filters require parsing the JSON
        if (levelFilter is null && sinceFilter is null && untilFilter is null)
            return true;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Level filter: @l property, defaults to "Information" if absent
            if (levelFilter is not null)
            {
                var entryLevel = root.TryGetProperty("@l", out var lProp) ? lProp.GetString() ?? "Information" : "Information";
                if (!levelFilter.Contains(entryLevel))
                    return false;
            }

            // Time range filter: @t property
            if (sinceFilter is not null || untilFilter is not null)
            {
                if (!root.TryGetProperty("@t", out var tProp))
                    return false;

                var ts = DateTimeOffset.Parse(tProp.GetString()!);
                if (sinceFilter is not null && ts < sinceFilter.Value)
                    return false;
                if (untilFilter is not null && ts > untilFilter.Value)
                    return false;
            }

            return true;
        }
        catch
        {
            // If the line can't be parsed, exclude it from filtered results
            return false;
        }
    }

    /// <summary>
    /// Parses comma-separated level filter into a set of canonical Serilog level names.
    /// Accepts both abbreviations (ERR, WRN) and full names (Error, Warning).
    /// </summary>
    private static HashSet<string>? ParseLevelFilter(string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return null;

        var result = new HashSet<string>();
        foreach (var part in level.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LevelAliases.TryGetValue(part, out var canonical))
                result.Add(canonical);
        }

        return result.Count > 0 ? result : null;
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

    [JsonPropertyName("matchedLines")]
    public int MatchedLines { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("lines")]
    public required List<string> Lines { get; init; }
}
