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
/// </summary>
public sealed class CompactionSummarizer : ICompactionSummarizer
{
    private readonly Func<string, ILlmTextProvider> _providerFactory;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<CompactionSummarizer> _logger;

    public CompactionSummarizer(
        Func<string, ILlmTextProvider> providerFactory,
        AgentConfig agentConfig,
        ILogger<CompactionSummarizer> logger)
    {
        _providerFactory = providerFactory;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public async Task<CompactionResult> SummarizeAsync(string? existingContext, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        var userContent = new StringBuilder();

        if (existingContext is not null)
        {
            userContent.AppendLine("## Existing Context (from previous compaction)");
            userContent.AppendLine(existingContext);
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
        }

        var llmMessages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = CompactionPrompt.System },
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
        var content = fullContent.ToString();
        using var doc = JsonDocument.Parse(content);
        var context = doc.RootElement.GetProperty("context").GetString()!;
        var memories = doc.RootElement.TryGetProperty("memories", out var mem)
            ? mem.EnumerateArray().Select(m => m.GetString()!).ToList()
            : new List<string>();

        _logger.LogInformation("Compaction summary generated: {Length} chars, {MemoryCount} memories", context.Length, memories.Count);

        return new CompactionResult { Context = context, Memories = memories };
    }
}
