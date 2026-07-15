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

    [Fact]
    public async Task SummarizeAsync_passes_configured_CompactionThinking_into_CompletionOptions()
    {
        var config = new AgentConfig
        {
            CompactionProvider = "set",
            CompactionModel = "set-model",
            CompactionThinking = "low"
        };
        var provider = new CapturingTextProvider("{\"context\": \"summary\"}");
        Func<string, ILlmTextProvider> factory = _ => provider;
        var summarizer = new CompactionSummarizer(factory, config, NullLogger<CompactionSummarizer>.Instance);

        await summarizer.SummarizeAsync(existingContext: null, messages: []);

        Assert.Equal("low", provider.LastOptions?.Thinking);
    }
}
