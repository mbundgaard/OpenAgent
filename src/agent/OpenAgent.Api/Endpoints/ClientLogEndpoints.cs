using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Receives batches of structured log entries from client apps (e.g. the iOS MAUI head)
/// and re-emits them through the host's ILogger so they land in the standard Serilog
/// daily log file. Already-existing /api/logs queries can find them via search=client.
/// </summary>
public static class ClientLogEndpoints
{
    private const int MaxLinesPerBatch = 500;

    /// <summary>Maps POST /api/client-logs.</summary>
    public static void MapClientLogEndpoints(this WebApplication app)
    {
        app.MapPost("/api/client-logs", (
            ClientLogBatch batch,
            ILoggerFactory loggerFactory) =>
        {
            if (batch.Lines is null || batch.Lines.Count == 0)
                return Results.Ok(new { accepted = 0 });
            if (batch.Lines.Count > MaxLinesPerBatch)
                return Results.BadRequest(new { error = $"Batch too large (max {MaxLinesPerBatch} lines)" });

            // Each line has its own category (e.g. "ApiClient", "VoiceWebSocketClient").
            // Group by category so we use one logger per category — Serilog records the category.
            foreach (var grouping in batch.Lines.GroupBy(l => l.Category ?? "client"))
            {
                var logger = loggerFactory.CreateLogger($"client.{grouping.Key}");
                foreach (var line in grouping)
                {
                    var level = ParseLevel(line.Level);
                    // Use a structured template so Serilog captures the timestamp and message
                    // as separate properties; the original client timestamp is preserved as ClientTs
                    // so server can tell when the event happened on the device vs when it was uploaded.
                    logger.Log(level, "[client] {ClientTs:o} {Message}",
                        line.Timestamp ?? DateTimeOffset.UtcNow, line.Message ?? "");
                }
            }

            return Results.Ok(new { accepted = batch.Lines.Count });
        }).RequireAuthorization();
    }

    private static LogLevel ParseLevel(string? level) =>
        level?.ToLowerInvariant() switch
        {
            "trace" or "verbose" or "vrb" => LogLevel.Trace,
            "debug" or "dbg" => LogLevel.Debug,
            "warning" or "warn" or "wrn" => LogLevel.Warning,
            "error" or "err" => LogLevel.Error,
            "critical" or "fatal" or "ftl" => LogLevel.Critical,
            _ => LogLevel.Information,
        };
}

/// <summary>Batch of structured log lines from a client app.</summary>
public sealed class ClientLogBatch
{
    [JsonPropertyName("lines")]
    public List<ClientLogLine>? Lines { get; init; }
}

/// <summary>Single log entry from a client app.</summary>
public sealed class ClientLogLine
{
    /// <summary>UTC timestamp when the event occurred on the client.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Log level: trace/debug/information/warning/error/critical.</summary>
    [JsonPropertyName("level")]
    public string? Level { get; init; }

    /// <summary>Logger category, e.g. "ApiClient" or "CallViewModel".</summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    /// <summary>Rendered log message.</summary>
    [JsonPropertyName("msg")]
    public string? Message { get; init; }
}
