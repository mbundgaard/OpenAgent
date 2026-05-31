using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.MemoryDigest;

/// <summary>Outcome of <see cref="MemoryDigestService.RunAsync"/>.</summary>
public sealed record DigestResult(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("ran")] bool Ran,
    [property: JsonPropertyName("operationsAttempted")] int OperationsAttempted,
    [property: JsonPropertyName("operationsApplied")] int OperationsApplied,
    [property: JsonPropertyName("skipped")] bool Skipped,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>
/// Nightly maintenance job. Reads DIGEST.md as the system prompt, MEMORY.md and the last 7
/// daily logs as the user payload, asks the configured compaction-tier LLM for a structured
/// JSON op list, applies it to MEMORY.md via <see cref="MarkdownSectionEditor"/>, and writes
/// an audit file under memory/digests/. Idempotent on date — a same-day re-run is a no-op.
/// </summary>
public sealed class MemoryDigestService
{
    private const int DailyLogWindow = 7;

    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly TimeZoneInfo CopenhagenTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    private readonly Func<string, ILlmTextProvider> _providerFactory;
    private readonly AgentConfig _agentConfig;
    private readonly AgentEnvironment _environment;
    private readonly ILogger<MemoryDigestService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private bool _providerUnsetLogged;

    public MemoryDigestService(
        Func<string, ILlmTextProvider> providerFactory,
        AgentConfig agentConfig,
        AgentEnvironment environment,
        ILogger<MemoryDigestService> logger)
    {
        _providerFactory = providerFactory;
        _agentConfig = agentConfig;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Run one digest cycle. Returns a result describing what happened. Never throws for
    /// expected skip conditions (disabled provider, already-run date, missing inputs) — only
    /// for genuine I/O or LLM failures.
    /// </summary>
    public async Task<DigestResult> RunAsync(CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            return await RunInternalAsync(ct);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<DigestResult> RunInternalAsync(CancellationToken ct)
    {
        var todayLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CopenhagenTz).Date;
        var dateString = todayLocal.ToString("yyyy-MM-dd");

        if (string.IsNullOrWhiteSpace(_agentConfig.CompactionProvider)
            || string.IsNullOrWhiteSpace(_agentConfig.CompactionModel))
        {
            if (!_providerUnsetLogged)
            {
                _providerUnsetLogged = true;
                _logger.LogWarning("Memory digest skipped: CompactionProvider or CompactionModel is unset");
            }
            return new DigestResult(dateString, Ran: false, 0, 0, Skipped: true, Reason: "compaction provider not configured");
        }

        var digestsDir = Path.Combine(_environment.DataPath, "memory", "digests");
        Directory.CreateDirectory(digestsDir);
        var auditPath = Path.Combine(digestsDir, $"{dateString}.json");
        if (File.Exists(auditPath))
        {
            _logger.LogDebug("Memory digest skipped: {Date} already has an audit file at {Path}", dateString, auditPath);
            return new DigestResult(dateString, Ran: false, 0, 0, Skipped: true, Reason: "already ran today");
        }

        var instructionsPath = Path.Combine(_environment.DataPath, "DIGEST.md");
        if (!File.Exists(instructionsPath))
        {
            _logger.LogWarning("Memory digest skipped: DIGEST.md not found at {Path}", instructionsPath);
            return new DigestResult(dateString, Ran: false, 0, 0, Skipped: true, Reason: "DIGEST.md not found");
        }

        var memoryPath = Path.Combine(_environment.DataPath, "MEMORY.md");
        var memoryContent = File.Exists(memoryPath) ? await File.ReadAllTextAsync(memoryPath, ct) : "";

        var memoryDir = Path.Combine(_environment.DataPath, "memory");
        var dailyLogs = LoadRecentDailyLogs(memoryDir, ct);

        var instructions = await File.ReadAllTextAsync(instructionsPath, ct);
        var userPayload = BuildUserPayload(dateString, memoryContent, dailyLogs);

        var raw = await CallLlmAsync(instructions, userPayload, ct);
        var (parsedDate, operations, parseError) = ParseOperations(raw);

        if (parseError is not null)
        {
            // Persist the raw response so the failure is debuggable but don't touch MEMORY.md.
            await WriteAuditAsync(auditPath, dateString, operations: [], results: [], rawResponse: raw, error: parseError, ct);
            _logger.LogError("Memory digest parse error: {Error}", parseError);
            return new DigestResult(dateString, Ran: true, 0, 0, Skipped: false, Reason: parseError);
        }

        if (operations.Count == 0)
        {
            await WriteAuditAsync(auditPath, parsedDate ?? dateString, operations, results: [], rawResponse: raw, error: null, ct);
            _logger.LogInformation("Memory digest: no operations needed for {Date}", dateString);
            return new DigestResult(dateString, Ran: true, 0, 0, Skipped: false, Reason: null);
        }

        var apply = MarkdownSectionEditor.Apply(memoryContent, operations);
        var appliedCount = apply.Results.Count(r => r.Applied);

        if (appliedCount > 0)
        {
            await File.WriteAllTextAsync(memoryPath, apply.Markdown, new UTF8Encoding(false), ct);
        }

        await WriteAuditAsync(auditPath, parsedDate ?? dateString, operations, apply.Results, rawResponse: raw, error: null, ct);

        _logger.LogInformation("Memory digest complete for {Date}: {Applied}/{Attempted} operations applied",
            dateString, appliedCount, operations.Count);

        return new DigestResult(dateString, Ran: true, operations.Count, appliedCount, Skipped: false, Reason: null);
    }

    private List<DailyLog> LoadRecentDailyLogs(string memoryDir, CancellationToken ct)
    {
        if (!Directory.Exists(memoryDir))
            return [];

        var logs = new List<DailyLog>();
        var files = Directory.GetFiles(memoryDir, "????-??-??.md")
            .OrderByDescending(f => Path.GetFileName(f))
            .Take(DailyLogWindow);

        foreach (var path in files)
        {
            if (ct.IsCancellationRequested) break;
            var date = Path.GetFileNameWithoutExtension(path);
            var content = File.ReadAllText(path);
            logs.Add(new DailyLog(date, content));
        }

        // Reverse so the prompt reads oldest → newest, matching how a human would scan history
        logs.Reverse();
        return logs;
    }

    private static string BuildUserPayload(string today, string memoryContent, IReadOnlyList<DailyLog> dailyLogs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Today is {today} (Europe/Copenhagen).");
        sb.AppendLine();
        sb.AppendLine("<current_memory>");
        sb.AppendLine(memoryContent.TrimEnd());
        sb.AppendLine("</current_memory>");
        sb.AppendLine();

        if (dailyLogs.Count == 0)
        {
            sb.AppendLine("<daily_logs note=\"no recent daily logs available\" />");
        }
        else
        {
            sb.AppendLine($"<daily_logs count=\"{dailyLogs.Count}\">");
            foreach (var log in dailyLogs)
            {
                sb.AppendLine($"<log date=\"{log.Date}\">");
                sb.AppendLine(log.Content.TrimEnd());
                sb.AppendLine("</log>");
            }
            sb.AppendLine("</daily_logs>");
        }

        sb.AppendLine();
        sb.AppendLine("Output the JSON object now.");
        return sb.ToString();
    }

    private async Task<string> CallLlmAsync(string systemPrompt, string userContent, CancellationToken ct)
    {
        var provider = _providerFactory(_agentConfig.CompactionProvider);
        var messages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = systemPrompt },
            new() { Id = "usr", ConversationId = "", Role = "user", Content = userContent }
        };
        var options = new CompletionOptions { ResponseFormat = "json_object" };

        var buffer = new StringBuilder();
        await foreach (var evt in provider.CompleteAsync(messages, _agentConfig.CompactionModel, options, ct))
        {
            if (evt is TextDelta delta)
                buffer.Append(delta.Content);
        }
        return buffer.ToString();
    }

    /// <summary>
    /// Extract the digest JSON from the LLM response. Mirrors the tolerance in CompactionSummarizer —
    /// raw JSON, code-fenced JSON, or a balanced object embedded in prose. Returns (date, operations,
    /// errorMessage). On parse failure, operations is empty and errorMessage explains why.
    /// </summary>
    internal static (string? Date, List<DigestOperation> Operations, string? Error) ParseOperations(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return (null, [], "LLM response was empty");

        var candidates = new List<string> { trimmed };
        var fenced = StripCodeFence(trimmed);
        if (fenced is not null) candidates.Add(fenced);
        var braceIdx = trimmed.IndexOf('{');
        if (braceIdx >= 0)
        {
            var balanced = ExtractBalancedJsonObject(trimmed, braceIdx);
            if (balanced is not null) candidates.Add(balanced);
        }

        foreach (var candidate in candidates)
        {
            if (TryParseJson(candidate, out var date, out var ops, out var err))
                return (date, ops, null);

            // Remember the most specific error for the final return if all candidates fail
            if (err is not null && candidate == candidates[^1])
                return (null, [], err);
        }

        return (null, [], "LLM response did not contain a parseable JSON digest object");
    }

    private static bool TryParseJson(string json, out string? date, out List<DigestOperation> operations, out string? error)
    {
        date = null;
        operations = [];
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root JSON value was not an object";
                return false;
            }

            if (root.TryGetProperty("date", out var dateProp) && dateProp.ValueKind == JsonValueKind.String)
                date = dateProp.GetString();

            if (!root.TryGetProperty("operations", out var opsProp))
            {
                error = "missing 'operations' array";
                return false;
            }

            if (opsProp.ValueKind != JsonValueKind.Array)
            {
                error = "'operations' is not an array";
                return false;
            }

            foreach (var op in opsProp.EnumerateArray())
            {
                if (op.ValueKind != JsonValueKind.Object) continue;

                var action = op.TryGetProperty("action", out var aProp) ? aProp.GetString() : null;
                var section = op.TryGetProperty("section", out var sProp) ? sProp.GetString() : null;
                var content = op.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String
                    ? cProp.GetString() : null;
                var old = op.TryGetProperty("old", out var oProp) && oProp.ValueKind == JsonValueKind.String
                    ? oProp.GetString() : null;
                var @new = op.TryGetProperty("new", out var nProp) && nProp.ValueKind == JsonValueKind.String
                    ? nProp.GetString() : null;

                if (string.IsNullOrWhiteSpace(action) || section is null)
                    continue;

                if (!TryParseAction(action, out var kind))
                    continue;

                operations.Add(new DigestOperation(kind, section, content, old, @new));
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"JSON parse failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseAction(string action, out DigestOperationKind kind)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "add": kind = DigestOperationKind.Add; return true;
            case "update": kind = DigestOperationKind.Update; return true;
            case "remove": case "delete": kind = DigestOperationKind.Remove; return true;
            default: kind = default; return false;
        }
    }

    private static string? StripCodeFence(string text)
    {
        if (!text.StartsWith("```")) return null;
        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0) return null;
        var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence <= firstNewline) return null;
        return text[(firstNewline + 1)..closingFence].Trim();
    }

    private static string? ExtractBalancedJsonObject(string text, int startIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = startIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped) { escaped = false; continue; }
            if (inString)
            {
                if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text[startIndex..(i + 1)];
            }
        }
        return null;
    }

    private static async Task WriteAuditAsync(
        string path,
        string date,
        IReadOnlyList<DigestOperation> operations,
        IReadOnlyList<DigestOperationResult> results,
        string rawResponse,
        string? error,
        CancellationToken ct)
    {
        var audit = new
        {
            date,
            generatedAt = DateTimeOffset.UtcNow,
            operations = operations.Select(op => new
            {
                action = op.Action.ToString().ToLowerInvariant(),
                section = op.Section,
                content = op.Content,
                old = op.Old,
                @new = op.New
            }),
            results = results.Select(r => new
            {
                applied = r.Applied,
                reason = r.Reason
            }),
            error,
            rawResponse
        };
        var json = JsonSerializer.Serialize(audit, AuditJsonOptions);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
    }

    private sealed record DailyLog(string Date, string Content);
}
