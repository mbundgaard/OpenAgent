using System.Text.Json.Serialization;

namespace OpenAgent.App.Core.Logging;

/// <summary>Single log entry shipped from the iOS app to the agent's <c>POST /api/client-logs</c>.</summary>
public sealed class ClientLogLine
{
    /// <summary>UTC timestamp when the event occurred on the client.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Log level: trace/debug/information/warning/error/critical.</summary>
    [JsonPropertyName("level")]
    public string Level { get; init; } = "Information";

    /// <summary>Logger category (e.g. "OpenAgent.App.Core.Api.ApiClient").</summary>
    [JsonPropertyName("category")]
    public string Category { get; init; } = "client";

    /// <summary>Rendered log message including any exception info.</summary>
    [JsonPropertyName("msg")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>Wire shape of a batch upload to <c>/api/client-logs</c>.</summary>
public sealed class ClientLogBatch
{
    [JsonPropertyName("lines")]
    public List<ClientLogLine> Lines { get; init; } = new();
}
