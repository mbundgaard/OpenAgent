using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryIndexServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _memoryDir;
    private readonly MemoryChunkStore _store;
    private readonly AgentEnvironment _env;

    public MemoryIndexServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openagent-memidx-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _memoryDir = Path.Combine(_tempDir, "memory");
        Directory.CreateDirectory(_memoryDir);
        _env = new AgentEnvironment { DataPath = _tempDir };
        _store = new MemoryChunkStore(Path.Combine(_tempDir, "memory.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private MemoryIndexService BuildService(
        string chunkerResponse,
        FakeEmbeddingProvider? embedding = null,
        int memoryDays = 3)
    {
        embedding ??= new FakeEmbeddingProvider();
        var config = new AgentConfig
        {
            CompactionProvider = "fake",
            CompactionModel = "fake-model",
            EmbeddingProvider = embedding.Key,
            MemoryDays = memoryDays,
        };
        var provider = new StreamingTextProvider(chunkerResponse);
        var chunker = new MemoryChunker(_ => provider, config);
        return new MemoryIndexService(
            _store,
            chunker,
            _ => embedding,
            config,
            _env,
            NullLogger<MemoryIndexService>.Instance);
    }

    private void WriteMemoryFile(string date, string body)
    {
        // Ensure we're above the 50-char minimum with padding text if the caller doesn't supply enough
        if (body.Length < 60) body += new string('x', 60 - body.Length);
        File.WriteAllText(Path.Combine(_memoryDir, $"{date}.md"), body);
    }

    [Fact]
    public async Task RunAsync_processes_files_past_the_window_and_deletes_source()
    {
        WriteMemoryFile("2026-04-18", "Today's notes about Alice.");
        WriteMemoryFile("2026-04-17", "Yesterday about Bob.");
        WriteMemoryFile("2026-04-16", "Two days ago about Carol.");
        WriteMemoryFile("2026-04-10", "A week ago about Dave.");

        const string response = """
            {"chunks":[{"content":"Dave chunk content.","summary":"About Dave"}]}
            """;
        var service = BuildService(response, memoryDays: 3);

        var result = await service.RunAsync();

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, result.FilesProcessed);
        Assert.Equal(1, result.ChunksCreated);
        Assert.Equal(0, result.Errors);

        Assert.False(File.Exists(Path.Combine(_memoryDir, "2026-04-10.md")), "indexed file should be out of the memory root");
        Assert.True(File.Exists(Path.Combine(_memoryDir, "backup", "2026-04-10.md")), "indexed file should be in backup/");
        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-16.md")), "in-window file should stay");
        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-17.md")), "in-window file should stay");
        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-18.md")), "today's file should stay");

        Assert.Contains("2026-04-10", _store.GetProcessedDates());
    }

    [Fact]
    public async Task RunAsync_skips_files_within_memory_window()
    {
        WriteMemoryFile("2026-04-18", "Today");
        WriteMemoryFile("2026-04-17", "Yesterday");
        WriteMemoryFile("2026-04-16", "Two days ago");

        var service = BuildService("""{"chunks":[{"content":"x","summary":"x"}]}""", memoryDays: 3);

        var result = await service.RunAsync();

        Assert.Equal(0, result.FilesScanned);
        Assert.Equal(0, result.FilesProcessed);
        Assert.Equal(0, result.ChunksCreated);
        Assert.Empty(_store.GetProcessedDates());
        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-16.md")));
    }

    [Fact]
    public async Task RunAsync_skips_dates_already_in_the_index()
    {
        WriteMemoryFile("2026-04-10", "Older content about indexed day.");
        _store.InsertChunks("2026-04-10", "fake", "fake-model", 4,
            [new ChunkEntry("existing", "existing summary", [0.1f, 0.2f, 0.3f, 0.4f])]);

        var service = BuildService("""{"chunks":[{"content":"new","summary":"new"}]}""", memoryDays: 3);

        // Enough fresh files to push 2026-04-10 past the window
        WriteMemoryFile("2026-04-18", "today");
        WriteMemoryFile("2026-04-17", "yesterday");
        WriteMemoryFile("2026-04-16", "two days ago");

        var result = await service.RunAsync();

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.FilesProcessed);
        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-10.md")),
            "file should be left in place when date already indexed");
    }

    [Fact]
    public async Task RunAsync_deletes_file_when_llm_signals_discard()
    {
        WriteMemoryFile("2026-04-10", "some unimportant scratch content");
        WriteMemoryFile("2026-04-18", "today");
        WriteMemoryFile("2026-04-17", "yesterday");
        WriteMemoryFile("2026-04-16", "two days ago");

        // LLM returns discard=true with no chunks — "not worth indexing, delete it"
        var service = BuildService("""{"chunks":[],"discard":true}""", memoryDays: 3);

        var result = await service.RunAsync();

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.FilesProcessed);
        Assert.Equal(1, result.FilesDiscarded);
        Assert.Equal(0, result.ChunksCreated);
        Assert.False(File.Exists(Path.Combine(_memoryDir, "2026-04-10.md")),
            "discard=true must delete the file without indexing");
        Assert.False(File.Exists(Path.Combine(_memoryDir, "backup", "2026-04-10.md")),
            "discard=true must NOT back up the file — LLM said it isn't worth keeping");
        Assert.DoesNotContain("2026-04-10", _store.GetProcessedDates());
    }

    [Fact]
    public async Task RunAsync_leaves_file_when_llm_returns_empty_chunks_without_discard()
    {
        WriteMemoryFile("2026-04-10", "real content that the chunker can't handle for some reason");
        WriteMemoryFile("2026-04-18", "today");
        WriteMemoryFile("2026-04-17", "yesterday");
        WriteMemoryFile("2026-04-16", "two days ago");

        // Empty chunks with no explicit discard signal — treat as transient failure
        var service = BuildService("""{"chunks":[]}""", memoryDays: 3);

        var result = await service.RunAsync();

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.FilesProcessed);
        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-10.md")),
            "no chunks + no discard means retry next run; file must stay on disk");
    }

    [Fact]
    public async Task RunAsync_returns_zero_counts_when_memory_directory_empty()
    {
        var service = BuildService("""{"chunks":[{"content":"x","summary":"x"}]}""", memoryDays: 3);

        var result = await service.RunAsync();

        Assert.Equal(0, result.FilesScanned);
        Assert.Equal(0, result.FilesProcessed);
        Assert.Equal(0, result.ChunksCreated);
    }

    [Fact]
    public async Task SearchAsync_ranks_by_cosine_similarity_against_stored_embeddings()
    {
        // Hand-inject two chunks with known embeddings so the ranking is deterministic
        _store.InsertChunks("2026-04-10", "fake", "fake-model", 4,[
            new ChunkEntry("Content about cats.", "Cat summary", [1f, 0f, 0f, 0f]),
            new ChunkEntry("Content about dogs.", "Dog summary", [0f, 1f, 0f, 0f]),
        ]);

        var embedding = new FakeEmbeddingProvider();
        embedding.Set("cats", EmbeddingPurpose.Search, [1f, 0f, 0f, 0f]);
        var service = BuildService("""{"chunks":[]}""", embedding);

        var results = await service.SearchAsync("cats", limit: 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("Cat summary", results[0].Summary);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task SearchAsync_keyword_boost_reranks_when_vector_is_tied()
    {
        // Both chunks have the same vector (cosine = 1 against the query), so ranking
        // hinges entirely on keyword overlap via the FTS5 leg.
        _store.InsertChunks("2026-04-10", "fake", "fake-model", 4,[
            new ChunkEntry("Generic text with no distinctive keywords here.", "Generic topic one", [1f, 0f, 0f, 0f]),
            new ChunkEntry("Text mentioning platypus repeatedly: platypus platypus.", "Platypus topic two", [1f, 0f, 0f, 0f]),
        ]);

        var embedding = new FakeEmbeddingProvider();
        embedding.Set("platypus", EmbeddingPurpose.Search, [1f, 0f, 0f, 0f]);
        var service = BuildService("""{"chunks":[]}""", embedding);

        var results = await service.SearchAsync("platypus", limit: 5);

        Assert.Equal(2, results.Count);
        Assert.Contains("Platypus", results[0].Summary);
        Assert.True(results[0].Score > results[1].Score, "keyword match must break the vector tie");
    }

    [Fact]
    public async Task RunAsync_serializes_concurrent_calls_no_duplicate_work()
    {
        // Fill window + one past-window file. If RunAsync didn't serialize, two tasks
        // racing would both see the file as unprocessed, both chunk+embed it, and the
        // second would hit the UNIQUE(date, chunk_index) constraint — visible as errors
        // in the second IndexResult.
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-18.md"), new string('a', 80));
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-17.md"), new string('b', 80));
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-16.md"), new string('c', 80));
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-10.md"), new string('z', 80));

        var service = BuildService("""{"chunks":[{"content":"the topic","summary":"topic"}]}""", memoryDays: 3);

        var t1 = service.RunAsync();
        var t2 = service.RunAsync();
        var results = await Task.WhenAll(t1, t2);

        // Exactly one run did the work; the other saw alreadyProcessed and skipped.
        var totalErrors = results.Sum(r => r.Errors);
        var totalProcessed = results.Sum(r => r.FilesProcessed);
        Assert.Equal(0, totalErrors);
        Assert.Equal(1, totalProcessed);
        Assert.Single(_store.GetAllChunks("fake", "fake-model"));
    }

    [Fact]
    public async Task LoadChunksAsync_returns_full_content_for_given_ids()
    {
        _store.InsertChunks("2026-04-10", "fake", "fake-model", 4,[
            new ChunkEntry("First full body.", "first", [0.1f, 0.1f, 0.1f, 0.1f]),
            new ChunkEntry("Second full body.", "second", [0.2f, 0.2f, 0.2f, 0.2f]),
        ]);

        var stored = _store.GetAllChunks("fake", "fake-model");
        var service = BuildService("""{"chunks":[]}""");

        var results = await service.LoadChunksAsync([stored[1].Id]);

        Assert.Single(results);
        Assert.Equal("Second full body.", results[0].Content);
        Assert.Equal("2026-04-10", results[0].Date);
    }
}
