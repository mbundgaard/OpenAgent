using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Compaction;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.Tests;

public class CompactionSummarizerTests
{
    [Fact]
    public async Task Unset_provider_throws_CompactionDisabledException()
    {
        var config = new AgentConfig(); // CompactionProvider = "", CompactionModel = ""
        Func<string, ILlmTextProvider> factory = _ => throw new InvalidOperationException("should not be called");
        var summarizer = new CompactionSummarizer(factory, config, NullLogger<CompactionSummarizer>.Instance);

        await Assert.ThrowsAsync<CompactionDisabledException>(() =>
            summarizer.SummarizeAsync(existingContext: null, messages: []));

        // Second call also throws — verifies the guard is idempotent.
        await Assert.ThrowsAsync<CompactionDisabledException>(() =>
            summarizer.SummarizeAsync(existingContext: null, messages: []));
    }

    [Fact]
    public async Task Unset_model_also_throws()
    {
        var config = new AgentConfig { CompactionProvider = "set", CompactionModel = "" };
        Func<string, ILlmTextProvider> factory = _ => throw new InvalidOperationException("should not be called");
        var summarizer = new CompactionSummarizer(factory, config, NullLogger<CompactionSummarizer>.Instance);

        await Assert.ThrowsAsync<CompactionDisabledException>(() =>
            summarizer.SummarizeAsync(existingContext: null, messages: []));
    }
}
