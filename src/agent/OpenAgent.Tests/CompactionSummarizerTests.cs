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

    [Fact]
    public async Task Plain_markdown_response_is_used_directly()
    {
        var summary = "## Topic (2026-07-16 09:00 - 09:30)\nWe decided X. [ref: m1, m2]";
        var result = await Summarize(summary);
        Assert.Equal(summary, result.Context);
    }

    [Fact]
    public async Task Valid_json_wrapper_is_still_extracted_for_backward_compatibility()
    {
        var result = await Summarize("{\"context\": \"## Topic\\nDecided X.\"}");
        Assert.Equal("## Topic\nDecided X.", result.Context);
    }

    [Fact]
    public async Task Malformed_json_wrapper_is_rejected_rather_than_stored_verbatim()
    {
        // The production poison: a `{"context": "..."}` attempt whose huge body has unescaped
        // newlines/quotes so JSON parsing fails. Must throw, not store the raw wrapper.
        var malformed = "{\"context\":\"## Session Start\nUnescaped \"quotes\" and newlines everywhere\"}";
        await Assert.ThrowsAsync<CompactionInvalidResultException>(() => SummarizeTask(malformed));
    }

    [Fact]
    public async Task Empty_response_throws_invalid_result()
    {
        await Assert.ThrowsAsync<CompactionInvalidResultException>(() => SummarizeTask("   "));
    }

    private static Task<CompactionResult> SummarizeTask(string providerResponse)
    {
        var config = new AgentConfig { CompactionProvider = "set", CompactionModel = "set-model" };
        var provider = new CapturingTextProvider(providerResponse);
        Func<string, ILlmTextProvider> factory = _ => provider;
        var summarizer = new CompactionSummarizer(factory, config, NullLogger<CompactionSummarizer>.Instance);
        return summarizer.SummarizeAsync(existingContext: null, messages: []);
    }

    private static async Task<CompactionResult> Summarize(string providerResponse)
        => await SummarizeTask(providerResponse);
}
