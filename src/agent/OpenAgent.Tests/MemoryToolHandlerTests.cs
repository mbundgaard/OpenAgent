using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryToolHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MemoryChunkStore _store;
    private readonly AgentEnvironment _env;
    private readonly FakeEmbeddingProvider _embedding;

    public MemoryToolHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openagent-memtool-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "memory"));
        _env = new AgentEnvironment { DataPath = _tempDir };
        _store = new MemoryChunkStore(Path.Combine(_tempDir, "memory.db"));
        _embedding = new FakeEmbeddingProvider();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private MemoryIndexService BuildService(AgentConfig config)
    {
        var provider = new StreamingTextProvider("""{"chunks":[]}""");
        var chunker = new MemoryChunker(_ => provider, config);
        return new MemoryIndexService(
            _store,
            chunker,
            _ => _embedding,
            config,
            _env,
            NullLogger<MemoryIndexService>.Instance);
    }

    [Fact]
    public void Exposes_two_tools_when_embedding_provider_configured()
    {
        var config = new AgentConfig { EmbeddingProvider = "fake" };
        var service = BuildService(config);

        var handler = new MemoryToolHandler(service, config);

        Assert.Equal(2, handler.Tools.Count);
        var names = handler.Tools.Select(t => t.Definition.Name).ToHashSet();
        Assert.Contains("search_memory", names);
        Assert.Contains("load_memory_chunks", names);
    }

    [Fact]
    public void Exposes_no_tools_when_embedding_provider_unset()
    {
        var config = new AgentConfig { EmbeddingProvider = "" };
        var service = BuildService(config);

        var handler = new MemoryToolHandler(service, config);

        Assert.Empty(handler.Tools);
    }

    [Fact]
    public async Task search_memory_returns_summaries_with_id_date_summary_score_shape()
    {
        _store.InsertChunks("2026-04-10", "fake", 4, [
            new ChunkEntry("Content about cats.", "Cat summary", [1f, 0f, 0f, 0f]),
        ]);
        _embedding.Set("cats", EmbeddingPurpose.Search, [1f, 0f, 0f, 0f]);

        var config = new AgentConfig { EmbeddingProvider = "fake" };
        var handler = new MemoryToolHandler(BuildService(config), config);
        var searchTool = handler.Tools.First(t => t.Definition.Name == "search_memory");

        var raw = await searchTool.ExecuteAsync("""{"query":"cats"}""", "conv-1");

        using var doc = JsonDocument.Parse(raw);
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        var first = results[0];
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("date", out _));
        Assert.Equal("Cat summary", first.GetProperty("summary").GetString());
        Assert.True(first.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public async Task search_memory_respects_limit_parameter()
    {
        for (var i = 0; i < 10; i++)
        {
            _store.InsertChunks($"2026-04-{i + 1:D2}", "fake", 4,
                [new ChunkEntry($"content {i}", $"summary {i}", [1f, 0f, 0f, 0f])]);
        }
        _embedding.Set("anything", EmbeddingPurpose.Search, [1f, 0f, 0f, 0f]);

        var config = new AgentConfig { EmbeddingProvider = "fake" };
        var handler = new MemoryToolHandler(BuildService(config), config);
        var searchTool = handler.Tools.First(t => t.Definition.Name == "search_memory");

        var raw = await searchTool.ExecuteAsync("""{"query":"anything","limit":3}""", "conv-1");

        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(3, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task load_memory_chunks_returns_full_content_by_id()
    {
        _store.InsertChunks("2026-04-10", "fake", 4, [
            new ChunkEntry("First body.", "first", [0.1f, 0.1f, 0.1f, 0.1f]),
            new ChunkEntry("Second body.", "second", [0.2f, 0.2f, 0.2f, 0.2f]),
        ]);
        var stored = _store.GetAllChunks("fake");

        var config = new AgentConfig { EmbeddingProvider = "fake" };
        var handler = new MemoryToolHandler(BuildService(config), config);
        var loadTool = handler.Tools.First(t => t.Definition.Name == "load_memory_chunks");

        var raw = await loadTool.ExecuteAsync($$"""{"ids":[{{stored[1].Id}}]}""", "conv-1");

        using var doc = JsonDocument.Parse(raw);
        var chunks = doc.RootElement.GetProperty("chunks");
        Assert.Equal(1, chunks.GetArrayLength());
        Assert.Equal("Second body.", chunks[0].GetProperty("content").GetString());
        Assert.Equal("2026-04-10", chunks[0].GetProperty("date").GetString());
    }
}
