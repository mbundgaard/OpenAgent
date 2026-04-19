using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>Outcome of a RunAsync invocation.</summary>
public sealed record IndexResult(
    [property: JsonPropertyName("filesScanned")] int FilesScanned,
    [property: JsonPropertyName("filesProcessed")] int FilesProcessed,
    [property: JsonPropertyName("chunksCreated")] int ChunksCreated,
    [property: JsonPropertyName("errors")] int Errors);

/// <summary>A single hit from SearchAsync — summary only, not full content.</summary>
public sealed record SearchResult(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("score")] float Score);

/// <summary>A single chunk with full content returned from LoadChunksAsync.</summary>
public sealed record LoadResult(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Orchestrates the memory-index pipeline: discover files past the prompt window, chunk them
/// via the LLM, embed each chunk, persist to SQLite, delete the source file. Also exposes
/// hybrid (vector + keyword) search over indexed chunks.
/// </summary>
public sealed class MemoryIndexService
{
    private const float VectorWeight = 0.7f;
    private const float KeywordWeight = 0.3f;

    private readonly MemoryChunkStore _store;
    private readonly MemoryChunker _chunker;
    private readonly Func<string, IEmbeddingProvider> _embeddingProviderFactory;
    private readonly AgentConfig _agentConfig;
    private readonly AgentEnvironment _environment;
    private readonly ILogger<MemoryIndexService> _logger;

    // Lazy-loaded chunks for vector search. Keyed by provider so switching providers
    // at runtime doesn't return stale or dimension-mismatched vectors.
    private readonly object _cacheLock = new();
    private string? _cachedProvider;
    private List<StoredChunk>? _cachedChunks;

    public MemoryIndexService(
        MemoryChunkStore store,
        MemoryChunker chunker,
        Func<string, IEmbeddingProvider> embeddingProviderFactory,
        AgentConfig agentConfig,
        AgentEnvironment environment,
        ILogger<MemoryIndexService> logger)
    {
        _store = store;
        _chunker = chunker;
        _embeddingProviderFactory = embeddingProviderFactory;
        _agentConfig = agentConfig;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Scan memory/*.md past the memoryDays window, chunk and embed each unprocessed file,
    /// persist the chunks, delete the source file. Returns counts for observability.
    /// On per-file errors, logs and continues — the source file stays on disk for retry.
    /// </summary>
    public async Task<IndexResult> RunAsync(CancellationToken ct = default)
    {
        var provider = _embeddingProviderFactory(_agentConfig.EmbeddingProvider);
        var memoryDir = Path.Combine(_environment.DataPath, "memory");
        if (!Directory.Exists(memoryDir))
        {
            _logger.LogInformation("Memory directory does not exist, nothing to index: {Path}", memoryDir);
            return new IndexResult(0, 0, 0, 0);
        }

        // Same ordering as SystemPromptBuilder — newest first by filename (YYYY-MM-DD sorts lexicographically)
        var allFiles = Directory.GetFiles(memoryDir, "????-??-??.md")
            .OrderByDescending(f => Path.GetFileName(f))
            .ToList();

        var windowSize = Math.Max(1, _agentConfig.MemoryDays);
        // Files INSIDE the window stay on disk and get loaded into the prompt.
        // Files PAST the window are candidates for indexing.
        var candidates = allFiles.Skip(windowSize).ToList();

        var alreadyProcessed = _store.GetProcessedDates();

        var filesProcessed = 0;
        var chunksCreated = 0;
        var errors = 0;

        foreach (var filePath in candidates)
        {
            if (ct.IsCancellationRequested) break;

            var fileName = Path.GetFileName(filePath);
            var date = Path.GetFileNameWithoutExtension(fileName);

            if (alreadyProcessed.Contains(date))
            {
                _logger.LogDebug("Skipping {Date} — already has chunks in index", date);
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(filePath, ct);
                var outcome = await _chunker.ChunkFileAsync(content, ct);

                if (outcome.Discard)
                {
                    // The LLM judged this file not worth preserving. Delete without indexing.
                    File.Delete(filePath);
                    _logger.LogInformation("Discarded {File} — LLM flagged as not worth indexing", fileName);
                    continue;
                }

                if (outcome.Chunks.Count == 0)
                {
                    // LLM didn't return chunks and didn't explicitly discard — treat as a
                    // transient failure and leave the file for the next run.
                    _logger.LogWarning("Chunker returned zero chunks for {File}; leaving file on disk for retry", fileName);
                    continue;
                }

                var entries = new List<ChunkEntry>(outcome.Chunks.Count);
                foreach (var c in outcome.Chunks)
                {
                    var embedding = await provider.GenerateEmbeddingAsync(c.Content, EmbeddingPurpose.Indexing, ct);
                    entries.Add(new ChunkEntry(c.Content, c.Summary, embedding));
                }

                _store.InsertChunks(date, provider.Key, provider.Dimensions, entries);
                File.Delete(filePath);
                filesProcessed++;
                chunksCreated += entries.Count;
                _logger.LogInformation("Indexed {File} into {Count} chunks", fileName, entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to index {File}; will retry next run", fileName);
                errors++;
            }
        }

        InvalidateCache();

        return new IndexResult(candidates.Count, filesProcessed, chunksCreated, errors);
    }

    /// <summary>
    /// Hybrid search: 0.7 * cosine similarity against current-provider embeddings +
    /// 0.3 * normalized FTS5 rank (provider-agnostic). Returns the top <paramref name="limit"/>
    /// matches ordered by combined score. Safe to call before any indexing — returns empty.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return [];

        var provider = _embeddingProviderFactory(_agentConfig.EmbeddingProvider);
        var chunks = GetOrLoadChunks(provider.Key);
        if (chunks.Count == 0 && _store.SearchFts(query).Count == 0)
            return [];

        var queryEmbedding = await provider.GenerateEmbeddingAsync(query, EmbeddingPurpose.Search, ct);

        // Vector scores — only populated for rows embedded by the current provider.
        var vectorScores = new Dictionary<int, float>(chunks.Count);
        foreach (var c in chunks)
            vectorScores[c.Id] = MemoryChunkStore.CosineSimilarity(queryEmbedding, c.Embedding);

        var keywordScores = _store.SearchFts(query);

        // Union of ids that scored on either side
        var allIds = new HashSet<int>(vectorScores.Keys);
        allIds.UnionWith(keywordScores.Keys);

        var combined = new List<(int Id, float Score)>(allIds.Count);
        foreach (var id in allIds)
        {
            var v = vectorScores.TryGetValue(id, out var vs) ? vs : 0f;
            var k = keywordScores.TryGetValue(id, out var ks) ? ks : 0f;
            combined.Add((id, VectorWeight * v + KeywordWeight * k));
        }

        var topIds = combined
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .ToList();

        // Pull metadata (date, summary) in one round-trip rather than looking each up from cache
        var chunkLookup = chunks.ToDictionary(c => c.Id);
        var missing = topIds.Where(t => !chunkLookup.ContainsKey(t.Id)).Select(t => t.Id).ToList();
        if (missing.Count > 0)
        {
            foreach (var sc in _store.GetChunksByIds(missing))
                chunkLookup[sc.Id] = sc;
        }

        var results = new List<SearchResult>(topIds.Count);
        foreach (var (id, score) in topIds)
        {
            if (!chunkLookup.TryGetValue(id, out var chunk)) continue;
            results.Add(new SearchResult(chunk.Id, chunk.Date, chunk.Summary, score));
        }
        return results;
    }

    /// <summary>Load full chunk content for the given ids. Provider-agnostic.</summary>
    public Task<List<LoadResult>> LoadChunksAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        var loaded = _store.GetChunksByIds(ids)
            .Select(c => new LoadResult(c.Id, c.Date, c.Content))
            .ToList();
        return Task.FromResult(loaded);
    }

    /// <summary>Aggregate statistics about the stored chunks.</summary>
    public ChunkStats GetStats() => _store.GetStats();

    private List<StoredChunk> GetOrLoadChunks(string providerKey)
    {
        lock (_cacheLock)
        {
            if (_cachedChunks is not null && _cachedProvider == providerKey)
                return _cachedChunks;

            _cachedProvider = providerKey;
            _cachedChunks = _store.GetAllChunks(providerKey);
            return _cachedChunks;
        }
    }

    private void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedChunks = null;
            _cachedProvider = null;
        }
    }
}
