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

        // Parse the JSON response
        using var doc = JsonDocument.Parse(fullContent.ToString());
        var context = doc.RootElement.GetProperty("context").GetString()!;

        _logger.LogInformation("Compaction summary generated: {Length} chars (mode: {Mode})",
            context.Length, existingContext is null ? "initial" : "update");

        return new CompactionResult { Context = context };
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
