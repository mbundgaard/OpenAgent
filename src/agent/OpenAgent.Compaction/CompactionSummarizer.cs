using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Calls the LLM via an ILlmTextProvider to generate a structured compaction summary.
/// Picks the Initial prompt for first compaction, Update prompt for subsequent iterations.
/// </summary>
public sealed class CompactionSummarizer : ICompactionSummarizer
{
    /// <summary>
    /// Cap on serialized tool-result content fed into the summarizer. Full output is not
    /// needed to extract "this tool ran and returned X"; clipping prevents a single huge
    /// result from dominating the summarizer's own context window.
    /// </summary>
    private const int ToolResultMaxChars = 2000;

    private readonly Func<string, ILlmTextProvider> _providerFactory;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<CompactionSummarizer> _logger;
    private bool _providerUnsetLogged;

    public CompactionSummarizer(
        Func<string, ILlmTextProvider> providerFactory,
        AgentConfig agentConfig,
        ILogger<CompactionSummarizer> logger)
    {
        _providerFactory = providerFactory;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public async Task<CompactionResult> SummarizeAsync(
        string? existingContext,
        IReadOnlyList<Message> messages,
        string? customInstructions = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_agentConfig.CompactionProvider)
            || string.IsNullOrWhiteSpace(_agentConfig.CompactionModel))
        {
            if (!_providerUnsetLogged)
            {
                _providerUnsetLogged = true;
                _logger.LogWarning(
                    "Compaction skipped: AgentConfig.CompactionProvider or CompactionModel is unset. " +
                    "Set both to enable automatic and manual compaction.");
            }
            throw new CompactionDisabledException();
        }

        var systemPrompt = existingContext is null
            ? CompactionPrompt.Initial
            : CompactionPrompt.Update;

        var userContent = new StringBuilder();

        if (existingContext is not null)
        {
            userContent.AppendLine("<previous-summary>");
            userContent.AppendLine(existingContext);
            userContent.AppendLine("</previous-summary>");
            userContent.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            userContent.AppendLine("<focus>");
            userContent.AppendLine(customInstructions.Trim());
            userContent.AppendLine("</focus>");
            userContent.AppendLine();
        }

        userContent.AppendLine("## Messages to Compact");
        foreach (var msg in messages)
        {
            userContent.AppendLine($"[{msg.Id}] [{msg.CreatedAt:yyyy-MM-dd HH:mm}] [{msg.Role}]: {msg.Content}");
            if (msg.ToolCalls is not null)
                userContent.AppendLine($"  Tool calls: {msg.ToolCalls}");
            if (msg.ToolCallId is not null)
                userContent.AppendLine($"  (tool result for call {msg.ToolCallId})");
            if (!string.IsNullOrEmpty(msg.FullToolResult))
                userContent.AppendLine($"  Full content: {TruncateForSummary(msg.FullToolResult, ToolResultMaxChars)}");
        }

        var llmMessages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = systemPrompt },
            new() { Id = "usr", ConversationId = "", Role = "user", Content = userContent.ToString() }
        };

        // Resolve the compaction provider and call it
        var provider = _providerFactory(_agentConfig.CompactionProvider);
        var options = new CompletionOptions { ResponseFormat = "json_object" };

        var fullContent = new StringBuilder();
        await foreach (var evt in provider.CompleteAsync(llmMessages, _agentConfig.CompactionModel, options, ct))
        {
            if (evt is TextDelta delta)
                fullContent.Append(delta.Content);
        }

        // Anthropic doesn't honor the json_object response format the way OpenAI does, so the
        // model sometimes returns the raw markdown summary instead of `{"context": "..."}`.
        // Be tolerant: try clean JSON, then code-fenced JSON, then a balanced {...} block,
        // and finally fall back to treating the entire response as the context string.
        var raw = fullContent.ToString();
        var context = ExtractContext(raw);

        _logger.LogInformation("Compaction summary generated: {Length} chars (mode: {Mode})",
            context.Length, existingContext is null ? "initial" : "update");

        return new CompactionResult { Context = context };
    }

    /// <summary>
    /// Recover the summary text from the LLM response. Tries (in order): the response as JSON
    /// with a `context` property, a ```json fenced block, the first balanced `{...}` block,
    /// and finally the whole response as plain text.
    /// </summary>
    private string ExtractContext(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return "";

        if (TryParseContextJson(trimmed, out var fromDirect))
            return fromDirect;

        var fenced = StripCodeFence(trimmed);
        if (fenced is not null && TryParseContextJson(fenced, out var fromFenced))
            return fromFenced;

        var firstBrace = trimmed.IndexOf('{');
        if (firstBrace >= 0)
        {
            var candidate = ExtractBalancedJsonObject(trimmed, firstBrace);
            if (candidate is not null && TryParseContextJson(candidate, out var fromBalanced))
                return fromBalanced;
        }

        // Fallback: the model gave us the summary directly without the JSON wrapper.
        // Use the raw text as the context — better than throwing away an otherwise-good summary.
        _logger.LogWarning("Compaction response was not valid JSON; using raw text as context. First 60 chars: {Preview}",
            trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…");
        return trimmed;
    }

    private static bool TryParseContextJson(string json, out string context)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("context", out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                context = prop.GetString() ?? "";
                return true;
            }
        }
        catch (JsonException)
        {
        }

        context = "";
        return false;
    }

    private static string? StripCodeFence(string text)
    {
        // Handle ```json ... ``` and bare ``` ... ``` fences. Anthropic likes wrapping JSON in
        // code fences when it does follow the instruction.
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

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text[startIndex..(i + 1)];
            }
        }

        return null;
    }

    private static string TruncateForSummary(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var truncated = text.Length - maxChars;
        return $"{text[..maxChars]}\n\n[... {truncated} more characters truncated]";
    }
}

/// <summary>
/// Thrown by <see cref="CompactionSummarizer"/> when compaction cannot run because the
/// compaction provider/model is unset in <c>AgentConfig</c>. Callers should treat this as
/// "skip compaction", not an error.
/// </summary>
public sealed class CompactionDisabledException : Exception
{
    public CompactionDisabledException()
        : base("Compaction is disabled because CompactionProvider or CompactionModel is unset.") { }
}
