using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryChunkerTests
{
    [Fact]
    public void ParseChunksResponse_extracts_content_and_summary_pairs()
    {
        const string json = """
            {
              "chunks": [
                { "content": "Had coffee with Alice.", "summary": "Morning chat with Alice" },
                { "content": "Fixed bug #42 in the parser.", "summary": "Parser fix" }
              ]
            }
            """;

        var outcome = MemoryChunker.ParseChunksResponse(json);

        Assert.False(outcome.Discard);
        Assert.Equal(2, outcome.Chunks.Count);
        Assert.Equal("Had coffee with Alice.", outcome.Chunks[0].Content);
        Assert.Equal("Morning chat with Alice", outcome.Chunks[0].Summary);
        Assert.Equal("Fixed bug #42 in the parser.", outcome.Chunks[1].Content);
        Assert.Equal("Parser fix", outcome.Chunks[1].Summary);
    }

    [Fact]
    public void ParseChunksResponse_empty_array_returns_empty_outcome_without_discard()
    {
        var outcome = MemoryChunker.ParseChunksResponse("""{ "chunks": [] }""");
        Assert.Empty(outcome.Chunks);
        Assert.False(outcome.Discard);
    }

    [Fact]
    public void ParseChunksResponse_discard_true_with_empty_chunks_signals_delete()
    {
        var outcome = MemoryChunker.ParseChunksResponse("""{ "chunks": [], "discard": true }""");
        Assert.Empty(outcome.Chunks);
        Assert.True(outcome.Discard);
    }

    [Fact]
    public void ParseChunksResponse_discard_defaults_to_false_when_missing()
    {
        var outcome = MemoryChunker.ParseChunksResponse("""{ "chunks": [{"content":"c","summary":"s"}] }""");
        Assert.False(outcome.Discard);
    }

    [Fact]
    public void ParseChunksResponse_missing_chunks_key_returns_empty()
    {
        var outcome = MemoryChunker.ParseChunksResponse("""{ "other": "field" }""");
        Assert.Empty(outcome.Chunks);
        Assert.False(outcome.Discard);
    }

    [Fact]
    public void ParseChunksResponse_empty_input_returns_empty_outcome()
    {
        Assert.Empty(MemoryChunker.ParseChunksResponse("").Chunks);
        Assert.Empty(MemoryChunker.ParseChunksResponse("   ").Chunks);
    }

    [Fact]
    public void ParseChunksResponse_tolerates_markdown_code_fence()
    {
        // Anthropic often wraps JSON in ```json ... ``` even when asked for structured output.
        // Seen live against claude-opus-4-6 during the first memory-index run.
        const string fenced = """
            ```json
            {
              "chunks": [
                { "content": "body text", "summary": "a summary" }
              ]
            }
            ```
            """;

        var outcome = MemoryChunker.ParseChunksResponse(fenced);

        Assert.Single(outcome.Chunks);
        Assert.Equal("body text", outcome.Chunks[0].Content);
    }

    [Fact]
    public void ParseChunksResponse_tolerates_leading_prose_before_json()
    {
        const string noisy = "Here's the chunked output:\n```\n{\"chunks\":[{\"content\":\"c\",\"summary\":\"s\"}]}\n```";

        var outcome = MemoryChunker.ParseChunksResponse(noisy);

        Assert.Single(outcome.Chunks);
        Assert.Equal("c", outcome.Chunks[0].Content);
    }

    [Fact]
    public void ParseChunksResponse_no_braces_returns_empty_outcome()
    {
        var outcome = MemoryChunker.ParseChunksResponse("the model forgot to return json");
        Assert.Empty(outcome.Chunks);
        Assert.False(outcome.Discard);
    }

    [Fact]
    public void ParseChunksResponse_skips_entries_missing_content_or_summary()
    {
        const string json = """
            {
              "chunks": [
                { "content": "has both", "summary": "ok" },
                { "content": "missing summary" },
                { "summary": "missing content" },
                { "content": "", "summary": "empty content" }
              ]
            }
            """;

        var outcome = MemoryChunker.ParseChunksResponse(json);

        Assert.Single(outcome.Chunks);
        Assert.Equal("has both", outcome.Chunks[0].Content);
    }

    [Fact]
    public async Task ChunkFileAsync_calls_LLM_and_returns_parsed_outcome()
    {
        const string modelResponse = """
            {"chunks":[{"content":"topic A","summary":"A"},{"content":"topic B","summary":"B"}]}
            """;

        var provider = new StreamingTextProvider(modelResponse);
        var config = new AgentConfig { CompactionProvider = "fake", CompactionModel = "fake-model" };

        var chunker = new MemoryChunker(_ => provider, config);

        var outcome = await chunker.ChunkFileAsync("raw memory file contents");

        Assert.Equal(2, outcome.Chunks.Count);
        Assert.False(outcome.Discard);
        Assert.Equal("topic A", outcome.Chunks[0].Content);
        Assert.Equal("B", outcome.Chunks[1].Summary);
    }
}
