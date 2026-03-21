using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Compaction;

/// <summary>
/// Calls the LLM to generate a structured compaction summary from conversation messages.
/// Temporarily accepts Func&lt;string, ILlmTextProvider&gt; and AgentConfig — will be
/// fully refactored in Task 8 to use ILlmTextProvider.CompleteAsync.
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

    public Task<CompactionResult> SummarizeAsync(string? existingContext, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        // Temporary stub — full implementation will be done in Task 8
        throw new NotImplementedException("CompactionSummarizer will be refactored in Task 8 to use ILlmTextProvider.");
    }
}
