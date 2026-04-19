using Microsoft.Data.Sqlite;
using OpenAgent.MemoryIndex;

namespace OpenAgent.Tests;

public class MemoryChunkStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly MemoryChunkStore _store;

    public MemoryChunkStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openagent-chunkstore-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "memory.db");
        _store = new MemoryChunkStore(_dbPath);
    }

    public void Dispose()
    {
        // SQLite keeps connections pooled; clear so temp files can be deleted on Windows
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static ChunkEntry Entry(string content, string summary, float[]? embedding = null) =>
        new(content, summary, embedding ?? [0.1f, 0.2f, 0.3f]);

    [Fact]
    public void InsertChunks_with_summaries_marks_date_as_processed()
    {
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[
            Entry("Morning coffee notes.", "Morning coffee"),
            Entry("Afternoon meeting.", "Meeting summary"),
        ]);

        var dates = _store.GetProcessedDates();

        Assert.Contains("2026-04-17", dates);
        Assert.Single(dates);
    }

    [Fact]
    public void InsertChunks_duplicate_date_chunk_index_throws()
    {
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[Entry("one", "one")]);

        Assert.Throws<SqliteException>(() =>
            _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[Entry("two", "two")]));
    }

    [Fact]
    public void Embedding_roundtrip_preserves_exact_values()
    {
        var original = new float[] { 0.1f, -0.5f, float.Epsilon, 1.234e-7f, -0f, 3.14159f };

        var bytes = MemoryChunkStore.SerializeEmbedding(original);
        var restored = MemoryChunkStore.DeserializeEmbedding(bytes);

        Assert.Equal(original.Length, restored.Length);
        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], restored[i]);
    }

    [Fact]
    public void GetAllChunks_for_provider_returns_content_summary_and_embeddings()
    {
        var embedding = new float[] { 0.5f, -0.5f, 0.25f };
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[
            new ChunkEntry("Chunk A.", "Summary A", embedding),
        ]);

        var chunks = _store.GetAllChunks("onnx", "test-model");

        Assert.Single(chunks);
        var c = chunks[0];
        Assert.Equal("2026-04-17", c.Date);
        Assert.Equal(0, c.ChunkIndex);
        Assert.Equal("Chunk A.", c.Content);
        Assert.Equal("Summary A", c.Summary);
        Assert.Equal(embedding, c.Embedding);
        Assert.Equal("onnx", c.Provider);
        Assert.Equal("test-model", c.Model);
        Assert.Equal(3, c.Dimensions);
    }

    [Fact]
    public void GetAllChunks_filters_by_provider()
    {
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[Entry("From onnx.", "onnx sum")]);
        _store.InsertChunks("2026-04-18", "azure", "test-model", 3,[Entry("From azure.", "azure sum")]);

        var onnxChunks = _store.GetAllChunks("onnx", "test-model");
        var azureChunks = _store.GetAllChunks("azure", "test-model");
        var otherChunks = _store.GetAllChunks("other", "test-model");

        Assert.Single(onnxChunks);
        Assert.Equal("From onnx.", onnxChunks[0].Content);
        Assert.Single(azureChunks);
        Assert.Equal("From azure.", azureChunks[0].Content);
        Assert.Empty(otherChunks);
    }

    [Fact]
    public void GetChunksByIds_returns_specific_chunks_regardless_of_provider()
    {
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[
            Entry("one", "sum1"),
            Entry("two", "sum2"),
            Entry("three", "sum3"),
        ]);
        _store.InsertChunks("2026-04-18", "azure", "test-model", 3,[Entry("four", "sum4")]);

        var all = _store.GetAllChunks("onnx", "test-model");
        var firstId = all[0].Id;
        var thirdId = all[2].Id;
        var azureId = _store.GetAllChunks("azure", "test-model")[0].Id;

        var result = _store.GetChunksByIds([firstId, thirdId, azureId]);

        Assert.Equal(3, result.Count);
        var contents = result.Select(r => r.Content).ToHashSet();
        Assert.Contains("one", contents);
        Assert.Contains("three", contents);
        Assert.Contains("four", contents);
    }

    [Fact]
    public void GetChunksByIds_empty_input_returns_empty()
    {
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[Entry("one", "sum1")]);
        Assert.Empty(_store.GetChunksByIds([]));
    }

    [Fact]
    public void SearchFts_matches_content_and_orders_by_score()
    {
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[
            Entry("The quick brown fox jumps.", "Animal story"),
            Entry("A lazy dog sleeps by the fire.", "Pet nap"),
            Entry("Lunar eclipse tonight at nine.", "Astronomy note"),
        ]);

        var scores = _store.SearchFts("fox");

        Assert.Single(scores);
        Assert.True(scores.Values.First() > 0, "FTS score for matching term should be positive");
    }

    [Fact]
    public void SearchFts_better_match_has_higher_normalized_score()
    {
        // The BM25-backed rank gives more-negative values to richer matches. The normalization
        // maps them into [0, 1) so a document packed with the search term should rank above a
        // document that mentions it only once.
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[
            Entry("fox fox fox fox fox chasing fox fox fox fox fox", "Many foxes"),
            Entry("A single fox at dusk.", "One fox"),
        ]);

        var scores = _store.SearchFts("fox");

        Assert.Equal(2, scores.Count);
        var ordered = scores.OrderByDescending(kv => kv.Value).ToList();
        Assert.Contains("Many", _store.GetAllChunks("onnx", "test-model").First(c => c.Id == ordered[0].Key).Summary);
        Assert.True(ordered[0].Value > ordered[1].Value);
    }

    [Fact]
    public void SearchFts_no_match_returns_empty()
    {
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[Entry("Completely different text.", "some sum")]);

        var scores = _store.SearchFts("quantum");

        Assert.Empty(scores);
    }

    [Fact]
    public void GetStats_empty_returns_zeros()
    {
        var stats = _store.GetStats();

        Assert.Equal(0, stats.TotalChunks);
        Assert.Equal(0, stats.TotalDays);
        Assert.Null(stats.OldestDate);
        Assert.Null(stats.NewestDate);
    }

    [Fact]
    public void GetStats_with_entries_reports_correct_counts_and_range()
    {
        _store.InsertChunks("2026-04-10", "onnx", "test-model", 3, [Entry("a", "a"), Entry("b", "b")]);
        _store.InsertChunks("2026-04-17", "onnx", "test-model", 3,[Entry("c", "c")]);
        _store.InsertChunks("2026-04-12", "azure", "test-model", 3, [Entry("d", "d")]);

        var stats = _store.GetStats();

        Assert.Equal(4, stats.TotalChunks);
        Assert.Equal(3, stats.TotalDays);
        Assert.Equal("2026-04-10", stats.OldestDate);
        Assert.Equal("2026-04-17", stats.NewestDate);
    }

    [Fact]
    public void CosineSimilarity_identical_vectors_is_one()
    {
        var v = new float[] { 1, 2, 3, 4 };
        Assert.Equal(1f, MemoryChunkStore.CosineSimilarity(v, v), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_orthogonal_vectors_is_zero()
    {
        Assert.Equal(0f, MemoryChunkStore.CosineSimilarity([1, 0], [0, 1]), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_zero_vector_returns_zero_not_nan()
    {
        Assert.Equal(0f, MemoryChunkStore.CosineSimilarity([0, 0, 0], [1, 2, 3]));
        Assert.Equal(0f, MemoryChunkStore.CosineSimilarity([1, 2, 3], [0, 0, 0]));
    }

    [Fact]
    public void CosineSimilarity_mismatched_length_returns_zero()
    {
        Assert.Equal(0f, MemoryChunkStore.CosineSimilarity([1, 2, 3], [1, 2]));
    }
}
