using OpenAgent.Contracts;
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

        var chunks = MemoryChunker.ParseChunksResponse(json);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Had coffee with Alice.", chunks[0].Content);
        Assert.Equal("Morning chat with Alice", chunks[0].Summary);
        Assert.Equal("Fixed bug #42 in the parser.", chunks[1].Content);
        Assert.Equal("Parser fix", chunks[1].Summary);
    }

    [Fact]
    public void ParseChunksResponse_empty_array_returns_empty_list()
    {
        Assert.Empty(MemoryChunker.ParseChunksResponse("""{ "chunks": [] }"""));
    }

    [Fact]
    public void ParseChunksResponse_missing_chunks_key_returns_empty_list()
    {
        Assert.Empty(MemoryChunker.ParseChunksResponse("""{ "other": "field" }"""));
    }

    [Fact]
    public void ParseChunksResponse_empty_input_returns_empty_list()
    {
        Assert.Empty(MemoryChunker.ParseChunksResponse(""));
        Assert.Empty(MemoryChunker.ParseChunksResponse("   "));
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

        var chunks = MemoryChunker.ParseChunksResponse(json);

        Assert.Single(chunks);
        Assert.Equal("has both", chunks[0].Content);
    }

    [Fact]
    public async Task ChunkFileAsync_calls_LLM_and_returns_parsed_chunks()
    {
        const string modelResponse = """
            {"chunks":[{"content":"topic A","summary":"A"},{"content":"topic B","summary":"B"}]}
            """;

        var provider = new StreamingTextProvider(modelResponse);
        var config = new AgentConfig { CompactionProvider = "fake", CompactionModel = "fake-model" };

        var chunker = new MemoryChunker(_ => provider, config);

        var chunks = await chunker.ChunkFileAsync("raw memory file contents");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("topic A", chunks[0].Content);
        Assert.Equal("B", chunks[1].Summary);
    }
}
