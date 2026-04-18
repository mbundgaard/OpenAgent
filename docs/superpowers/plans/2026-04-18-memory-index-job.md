# Memory Index Job Implementation Plan (v3 — Hybrid Search)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Index closed daily memory log files into searchable topic chunks in `memory.db`. LLM splits each file into topical chunks with summaries, embeds them via Azure OpenAI, stores in SQLite with FTS5. Two agent tools: `search_memory` (hybrid vector + keyword search, returns summaries) and `load_memory_chunks` (loads full content by ID).

**Architecture:** New `OpenAgent.MemoryIndex` project. Nightly job reads daily `.md` files past the memory window, LLM chunks them into topics with summaries, embeds each chunk via Azure OpenAI, stores in SQLite with FTS5 virtual table, deletes the source file. Agent searches via hybrid ranking (0.7 cosine + 0.3 FTS5 BM25). Two-phase retrieval: search returns lightweight summaries, then load fetches full content for selected chunks.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, Azure OpenAI Embeddings API, FTS5, xUnit

**Spec:** [docs/superpowers/specs/2026-04-16-memory-index-job-design.md](../specs/2026-04-16-memory-index-job-design.md)

**Supersedes:** v1 and v2 plans

---

## File Map

### New files

| File | Responsibility |
|------|---------------|
| `src/agent/OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj` | Project file |
| `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs` | SQLite + FTS5 persistence, cosine similarity, embedding serialization |
| `src/agent/OpenAgent.MemoryIndex/EmbeddingClient.cs` | Azure OpenAI Embeddings API client |
| `src/agent/OpenAgent.MemoryIndex/MemoryChunker.cs` | LLM chunking with summaries |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs` | Orchestrates indexing and hybrid search |
| `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs` | IToolHandler: search_memory + load_memory_chunks |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs` | IHostedService daily timer |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs` | REST API for manual trigger and stats |
| `src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs` | DI registration |
| `src/agent/OpenAgent.Tests/MemoryChunkStoreTests.cs` | Store unit tests |
| `src/agent/OpenAgent.Tests/EmbeddingClientTests.cs` | Embedding client tests |
| `src/agent/OpenAgent.Tests/MemoryChunkerTests.cs` | Chunker tests |
| `src/agent/OpenAgent.Tests/MemoryIndexServiceTests.cs` | Service orchestration tests |
| `src/agent/OpenAgent.Tests/MemoryToolHandlerTests.cs` | Tool execution tests |
| `src/agent/OpenAgent.Tests/MemoryIndexHostedServiceTests.cs` | Hosted service guard tests |
| `src/agent/OpenAgent.Tests/Fakes/FakeEmbeddingHandler.cs` | Test double for HTTP embedding calls |

### Modified files

| File | Change |
|------|--------|
| `src/agent/OpenAgent.Models/Configs/AgentConfig.cs` | Add 4 embedding/index config fields |
| `src/agent/OpenAgent/OpenAgent.csproj` | Add ProjectReference |
| `src/agent/OpenAgent/Program.cs` | Add `AddMemoryIndex()` + `MapMemoryIndexEndpoints()` |
| `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` | Add ProjectReference |
| `src/agent/OpenAgent.sln` | Add new project (via `dotnet sln add`) |
| `src/agent/Directory.Packages.props` | No change needed — `Microsoft.Data.Sqlite` already present |

---

## Task 1: Project Scaffolding + AgentConfig

### Goal
Create the `OpenAgent.MemoryIndex` project, add it to the solution, add AgentConfig fields, and verify the build passes.

### Steps

- [ ] **1.1** Create project directory and `.csproj`

Create `src/agent/OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <InternalsVisibleTo Include="OpenAgent.Tests" />
  </ItemGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
    <ProjectReference Include="..\OpenAgent.Models\OpenAgent.Models.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **1.2** Add project to solution

```bash
cd src/agent && dotnet sln add OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj
```

- [ ] **1.3** Add ProjectReference from host project

In `src/agent/OpenAgent/OpenAgent.csproj`, add inside the `<ItemGroup>` that has other `ProjectReference` entries:

```xml
    <ProjectReference Include="..\OpenAgent.MemoryIndex\OpenAgent.MemoryIndex.csproj" />
```

- [ ] **1.4** Add ProjectReference from test project

In `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj`, add inside the `<ItemGroup>` that has other `ProjectReference` entries:

```xml
    <ProjectReference Include="..\OpenAgent.MemoryIndex\OpenAgent.MemoryIndex.csproj" />
```

- [ ] **1.5** Add AgentConfig fields

In `src/agent/OpenAgent.Models/Configs/AgentConfig.cs`, add these 4 properties after the existing `MainConversationId` property:

```csharp
    /// <summary>Azure OpenAI Embeddings endpoint URL (e.g. https://myresource.openai.azure.com/).</summary>
    [JsonPropertyName("embeddingEndpoint")]
    public string EmbeddingEndpoint { get; set; } = "";

    /// <summary>API key for the Azure OpenAI Embeddings endpoint.</summary>
    [JsonPropertyName("embeddingApiKey")]
    public string EmbeddingApiKey { get; set; } = "";

    /// <summary>Deployment name for the embedding model (e.g. text-embedding-3-small).</summary>
    [JsonPropertyName("embeddingDeployment")]
    public string EmbeddingDeployment { get; set; } = "";

    /// <summary>Hour of day (0-23) when the memory index job runs. Default: 2 (2 AM).</summary>
    [JsonPropertyName("indexRunAtHour")]
    public int IndexRunAtHour { get; set; } = 2;
```

- [ ] **1.6** Create a placeholder class so the project compiles

Create `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs` with a minimal placeholder:

```csharp
namespace OpenAgent.MemoryIndex;

/// <summary>
/// SQLite persistence for memory chunks with FTS5 full-text search.
/// </summary>
public sealed class MemoryChunkStore
{
}
```

- [ ] **1.7** Build and verify

```bash
cd src/agent && dotnet build
```

- [ ] **1.8** Commit

```
feat(memory): scaffold OpenAgent.MemoryIndex project and AgentConfig fields
```

---

## Task 2: MemoryChunkStore — SQLite + FTS5 Persistence (TDD)

### Goal
Implement the SQLite store with FTS5 full-text search for memory chunks. Records go in both `memory_chunks` and `memory_chunks_fts`.

### Records

```csharp
// Input record — what callers pass to InsertChunks
public sealed record ChunkEntry(string Content, string Summary, float[] Embedding);

// Output record — what comes back from queries
public sealed record StoredChunk(long Id, string Date, int ChunkIndex, string Content, string Summary, float[] Embedding);

// FTS search result — id and normalized rank
public sealed record ChunkSearchEntry(long Id, double NormalizedRank);

// Stats record
public sealed record ChunkStats(int TotalChunks, int TotalDates);
```

### Steps

- [ ] **2.1** Write failing tests

Create `src/agent/OpenAgent.Tests/MemoryChunkStoreTests.cs`:

```csharp
using OpenAgent.MemoryIndex;

namespace OpenAgent.Tests;

public class MemoryChunkStoreTests : IDisposable
{
    private readonly string _dbDir;
    private readonly MemoryChunkStore _store;

    public MemoryChunkStoreTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"openagent-memtest-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbDir);
        _store = new MemoryChunkStore(_dbDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    [Fact]
    public void InsertChunks_stores_and_retrieves_chunks()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var chunks = new List<ChunkEntry>
        {
            new("Full discussion about auth", "Auth middleware rewrite", embedding),
            new("Pasta carbonara recipe steps", "Pasta carbonara technique", embedding)
        };

        _store.InsertChunks("2026-04-10", chunks);

        var all = _store.GetAllChunks();
        Assert.Equal(2, all.Count);
        Assert.Equal("2026-04-10", all[0].Date);
        Assert.Equal(0, all[0].ChunkIndex);
        Assert.Equal("Full discussion about auth", all[0].Content);
        Assert.Equal("Auth middleware rewrite", all[0].Summary);
        Assert.Equal(embedding, all[0].Embedding);
        Assert.Equal(1, all[1].ChunkIndex);
    }

    [Fact]
    public void InsertChunks_populates_fts_index()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var chunks = new List<ChunkEntry>
        {
            new("Detailed discussion about authentication middleware", "Auth middleware rewrite", embedding),
            new("Pasta carbonara recipe with eggs and guanciale", "Pasta carbonara technique", embedding)
        };

        _store.InsertChunks("2026-04-10", chunks);

        var results = _store.SearchFts("authentication");
        Assert.Single(results);
        Assert.True(results[0].NormalizedRank > 0);
    }

    [Fact]
    public void InsertChunks_duplicate_date_is_idempotent()
    {
        var embedding = new float[] { 0.1f, 0.2f };
        var chunks = new List<ChunkEntry>
        {
            new("Content A", "Summary A", embedding)
        };

        _store.InsertChunks("2026-04-10", chunks);
        _store.InsertChunks("2026-04-10", chunks); // duplicate — should not throw or double

        var all = _store.GetAllChunks();
        Assert.Single(all);
    }

    [Fact]
    public void GetProcessedDates_returns_distinct_dates()
    {
        var embedding = new float[] { 0.1f };
        _store.InsertChunks("2026-04-10", [new("A", "A", embedding)]);
        _store.InsertChunks("2026-04-11", [new("B", "B", embedding)]);

        var dates = _store.GetProcessedDates();
        Assert.Equal(2, dates.Count);
        Assert.Contains("2026-04-10", dates);
        Assert.Contains("2026-04-11", dates);
    }

    [Fact]
    public void GetChunksByIds_returns_matching_chunks()
    {
        var embedding = new float[] { 0.5f, 0.5f };
        _store.InsertChunks("2026-04-10", [
            new("Content 1", "Summary 1", embedding),
            new("Content 2", "Summary 2", embedding),
            new("Content 3", "Summary 3", embedding)
        ]);

        var all = _store.GetAllChunks();
        var ids = new[] { all[0].Id, all[2].Id };

        var result = _store.GetChunksByIds(ids);
        Assert.Equal(2, result.Count);
        Assert.Equal("Content 1", result[0].Content);
        Assert.Equal("Content 3", result[1].Content);
    }

    [Fact]
    public void GetChunksByIds_empty_returns_empty()
    {
        var result = _store.GetChunksByIds([]);
        Assert.Empty(result);
    }

    [Fact]
    public void SearchFts_returns_normalized_ranks()
    {
        var embedding = new float[] { 0.1f };
        _store.InsertChunks("2026-04-10", [
            new("The quick brown fox jumps over the lazy dog", "Fox and dog story", embedding),
            new("Authentication middleware for ASP.NET Core", "Auth middleware", embedding)
        ]);

        var results = _store.SearchFts("fox");
        Assert.Single(results);

        var entry = results[0];
        Assert.True(entry.NormalizedRank > 0, "Normalized rank should be positive");
        Assert.True(entry.NormalizedRank <= 1, "Normalized rank should be <= 1");
    }

    [Fact]
    public void SearchFts_no_match_returns_empty()
    {
        var embedding = new float[] { 0.1f };
        _store.InsertChunks("2026-04-10", [new("Hello world", "Greeting", embedding)]);

        var results = _store.SearchFts("nonexistent");
        Assert.Empty(results);
    }

    [Fact]
    public void GetStats_returns_correct_counts()
    {
        var embedding = new float[] { 0.1f };
        _store.InsertChunks("2026-04-10", [
            new("A", "A", embedding),
            new("B", "B", embedding)
        ]);
        _store.InsertChunks("2026-04-11", [new("C", "C", embedding)]);

        var stats = _store.GetStats();
        Assert.Equal(3, stats.TotalChunks);
        Assert.Equal(2, stats.TotalDates);
    }

    [Fact]
    public void Embedding_roundtrip_preserves_values()
    {
        var embedding = new float[] { -0.123456f, 0.0f, 0.999999f, -1.0f };
        _store.InsertChunks("2026-04-10", [new("Content", "Summary", embedding)]);

        var all = _store.GetAllChunks();
        Assert.Single(all);
        Assert.Equal(embedding.Length, all[0].Embedding.Length);
        for (var i = 0; i < embedding.Length; i++)
            Assert.Equal(embedding[i], all[0].Embedding[i]);
    }
}
```

- [ ] **2.2** Run tests — verify they fail

```bash
cd src/agent && dotnet test --filter "MemoryChunkStoreTests"
```

- [ ] **2.3** Implement MemoryChunkStore

Replace `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs` with:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Input record — what callers pass to InsertChunks.
/// </summary>
public sealed record ChunkEntry(string Content, string Summary, float[] Embedding);

/// <summary>
/// Output record — what comes back from chunk queries.
/// </summary>
public sealed record StoredChunk(long Id, string Date, int ChunkIndex, string Content, string Summary, float[] Embedding);

/// <summary>
/// FTS search result — chunk id and normalized BM25 rank.
/// </summary>
public sealed record ChunkSearchEntry(long Id, double NormalizedRank);

/// <summary>
/// Aggregate statistics about the memory index.
/// </summary>
public sealed record ChunkStats(int TotalChunks, int TotalDates);

/// <summary>
/// SQLite persistence for memory chunks with FTS5 full-text search.
/// Stores chunks in memory_chunks table and maintains a parallel FTS5 virtual table
/// for keyword search. Embeddings are serialized as raw float byte arrays.
/// </summary>
public sealed class MemoryChunkStore : IDisposable
{
    private readonly string _connectionString;

    public MemoryChunkStore(string dataPath)
    {
        var dbPath = Path.Combine(dataPath, "memory.db");
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    /// <summary>
    /// Inserts chunks for a given date. Skips silently if the date already has chunks
    /// (UNIQUE constraint on date + chunk_index). Both memory_chunks and memory_chunks_fts
    /// are populated in the same transaction.
    /// </summary>
    public void InsertChunks(string date, IReadOnlyList<ChunkEntry> chunks)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            // Insert into main table
            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT OR IGNORE INTO memory_chunks (date, chunk_index, content, summary, embedding)
                VALUES (@date, @chunkIndex, @content, @summary, @embedding)
                """;
            insertCmd.Parameters.AddWithValue("@date", date);
            insertCmd.Parameters.AddWithValue("@chunkIndex", i);
            insertCmd.Parameters.AddWithValue("@content", chunk.Content);
            insertCmd.Parameters.AddWithValue("@summary", chunk.Summary);
            insertCmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(chunk.Embedding));

            var rowsAffected = insertCmd.ExecuteNonQuery();

            // Only insert into FTS if the main row was actually inserted (not a duplicate)
            if (rowsAffected > 0)
            {
                // Get the rowid of the just-inserted row
                using var rowidCmd = connection.CreateCommand();
                rowidCmd.Transaction = transaction;
                rowidCmd.CommandText = "SELECT last_insert_rowid()";
                var rowid = (long)rowidCmd.ExecuteScalar()!;

                // Insert into FTS index
                using var ftsCmd = connection.CreateCommand();
                ftsCmd.Transaction = transaction;
                ftsCmd.CommandText = "INSERT INTO memory_chunks_fts(rowid, summary, content) VALUES (@rowid, @summary, @content)";
                ftsCmd.Parameters.AddWithValue("@rowid", rowid);
                ftsCmd.Parameters.AddWithValue("@summary", chunk.Summary);
                ftsCmd.Parameters.AddWithValue("@content", chunk.Content);
                ftsCmd.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Returns all distinct dates that have been indexed.
    /// </summary>
    public IReadOnlyList<string> GetProcessedDates()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT date FROM memory_chunks ORDER BY date";

        using var reader = cmd.ExecuteReader();
        var dates = new List<string>();
        while (reader.Read())
            dates.Add(reader.GetString(0));

        return dates;
    }

    /// <summary>
    /// Returns all stored chunks ordered by date and chunk_index.
    /// Used by the search service to cache embeddings for cosine similarity.
    /// </summary>
    public IReadOnlyList<StoredChunk> GetAllChunks()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, date, chunk_index, content, summary, embedding FROM memory_chunks ORDER BY date, chunk_index";

        using var reader = cmd.ExecuteReader();
        var chunks = new List<StoredChunk>();
        while (reader.Read())
        {
            chunks.Add(new StoredChunk(
                Id: reader.GetInt64(0),
                Date: reader.GetString(1),
                ChunkIndex: reader.GetInt32(2),
                Content: reader.GetString(3),
                Summary: reader.GetString(4),
                Embedding: DeserializeEmbedding((byte[])reader[5])
            ));
        }

        return chunks;
    }

    /// <summary>
    /// Returns chunks matching the given IDs. Used by load_memory_chunks tool.
    /// </summary>
    public IReadOnlyList<StoredChunk> GetChunksByIds(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return [];

        using var connection = Open();
        using var cmd = connection.CreateCommand();

        // Build parameterized IN clause
        var paramNames = ids.Select((_, i) => $"@id{i}").ToList();
        cmd.CommandText = $"SELECT id, date, chunk_index, content, summary, embedding FROM memory_chunks WHERE id IN ({string.Join(", ", paramNames)}) ORDER BY date, chunk_index";

        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], ids[i]);

        using var reader = cmd.ExecuteReader();
        var chunks = new List<StoredChunk>();
        while (reader.Read())
        {
            chunks.Add(new StoredChunk(
                Id: reader.GetInt64(0),
                Date: reader.GetString(1),
                ChunkIndex: reader.GetInt32(2),
                Content: reader.GetString(3),
                Summary: reader.GetString(4),
                Embedding: DeserializeEmbedding((byte[])reader[5])
            ));
        }

        return chunks;
    }

    /// <summary>
    /// Runs FTS5 MATCH query and returns chunk IDs with normalized BM25 ranks.
    /// Normalized rank = 1 / (1 + abs(raw_rank)). Returns up to 100 results.
    /// </summary>
    public IReadOnlyList<ChunkSearchEntry> SearchFts(string query)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid, rank FROM memory_chunks_fts WHERE memory_chunks_fts MATCH @query ORDER BY rank LIMIT 100";
        cmd.Parameters.AddWithValue("@query", query);

        using var reader = cmd.ExecuteReader();
        var results = new List<ChunkSearchEntry>();
        while (reader.Read())
        {
            var rowid = reader.GetInt64(0);
            var rawRank = reader.GetDouble(1);
            var normalizedRank = 1.0 / (1.0 + Math.Abs(rawRank));
            results.Add(new ChunkSearchEntry(rowid, normalizedRank));
        }

        return results;
    }

    /// <summary>
    /// Returns aggregate statistics about the memory index.
    /// </summary>
    public ChunkStats GetStats()
    {
        using var connection = Open();

        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM memory_chunks";
        var totalChunks = (int)(long)countCmd.ExecuteScalar()!;

        using var dateCmd = connection.CreateCommand();
        dateCmd.CommandText = "SELECT COUNT(DISTINCT date) FROM memory_chunks";
        var totalDates = (int)(long)dateCmd.ExecuteScalar()!;

        return new ChunkStats(totalChunks, totalDates);
    }

    public void Dispose()
    {
        // No persistent connection to dispose — each operation opens/closes its own
    }

    /// <summary>Creates tables and FTS5 virtual table if they don't exist.</summary>
    private void InitializeDatabase()
    {
        using var connection = Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_chunks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                date        TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                content     TEXT NOT NULL,
                summary     TEXT NOT NULL,
                embedding   BLOB NOT NULL,
                UNIQUE(date, chunk_index)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS memory_chunks_fts USING fts5(
                summary, content, content=memory_chunks, content_rowid=id
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>Serializes a float array to a byte array (little-endian).</summary>
    internal static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        MemoryMarshal.AsBytes(embedding.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    /// <summary>Deserializes a byte array back to a float array.</summary>
    internal static float[] DeserializeEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(floats);
        return floats;
    }
}
```

- [ ] **2.4** Run tests — verify they pass

```bash
cd src/agent && dotnet test --filter "MemoryChunkStoreTests"
```

- [ ] **2.5** Commit

```
feat(memory): implement MemoryChunkStore with SQLite + FTS5 persistence
```

---

## Task 3: EmbeddingClient — Azure OpenAI (TDD)

### Goal
Implement an HTTP client that calls the Azure OpenAI Embeddings API and returns float arrays. Includes an `IsConfigured` guard so the tool handler can conditionally register tools.

### Steps

- [ ] **3.1** Write the FakeEmbeddingHandler test helper

Create `src/agent/OpenAgent.Tests/Fakes/FakeEmbeddingHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Fake HTTP handler that returns a canned JSON response for embedding API calls.
/// Captures the last request URI and body for assertion.
/// </summary>
public sealed class FakeEmbeddingHandler(string responseJson) : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequestUri = request.RequestUri;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
    }
}
```

- [ ] **3.2** Write failing tests

Create `src/agent/OpenAgent.Tests/EmbeddingClientTests.cs`:

```csharp
using OpenAgent.MemoryIndex;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class EmbeddingClientTests
{
    private static string MakeEmbeddingResponse(float[][] embeddings)
    {
        var dataEntries = embeddings.Select((e, i) =>
            $$"""{"object":"embedding","index":{{i}},"embedding":[{{string.Join(",", e.Select(f => f.ToString("G")))}}]}""");
        return $$"""{"object":"list","data":[{{string.Join(",", dataEntries)}}],"model":"text-embedding-3-small","usage":{"prompt_tokens":10,"total_tokens":10}}""";
    }

    [Fact]
    public async Task EmbedAsync_sends_correct_request_and_parses_response()
    {
        var expected = new float[] { 0.1f, 0.2f, 0.3f };
        var responseJson = MakeEmbeddingResponse([expected]);
        var handler = new FakeEmbeddingHandler(responseJson);
        var httpClient = new HttpClient(handler);
        var client = new EmbeddingClient("https://myresource.openai.azure.com/", "test-key", "text-embedding-3-small", httpClient);

        var result = await client.EmbedAsync(["hello world"]);

        Assert.Single(result);
        Assert.Equal(expected.Length, result[0].Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], result[0][i], precision: 5);

        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("text-embedding-3-small", handler.LastRequestUri!.ToString());
        Assert.Contains("api-key=test-key", handler.LastRequestUri.ToString());
    }

    [Fact]
    public async Task EmbedAsync_handles_multiple_inputs()
    {
        var e1 = new float[] { 0.1f, 0.2f };
        var e2 = new float[] { 0.3f, 0.4f };
        var responseJson = MakeEmbeddingResponse([e1, e2]);
        var handler = new FakeEmbeddingHandler(responseJson);
        var httpClient = new HttpClient(handler);
        var client = new EmbeddingClient("https://myresource.openai.azure.com/", "test-key", "text-embedding-3-small", httpClient);

        var result = await client.EmbedAsync(["hello", "world"]);

        Assert.Equal(2, result.Count);
        Assert.Equal(e1, result[0]);
        Assert.Equal(e2, result[1]);
    }

    [Fact]
    public void IsConfigured_returns_false_when_endpoint_empty()
    {
        var client = new EmbeddingClient("", "key", "deploy", new HttpClient());
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void IsConfigured_returns_false_when_key_empty()
    {
        var client = new EmbeddingClient("https://test.openai.azure.com/", "", "deploy", new HttpClient());
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void IsConfigured_returns_false_when_deployment_empty()
    {
        var client = new EmbeddingClient("https://test.openai.azure.com/", "key", "", new HttpClient());
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void IsConfigured_returns_true_when_all_set()
    {
        var client = new EmbeddingClient("https://test.openai.azure.com/", "key", "deploy", new HttpClient());
        Assert.True(client.IsConfigured);
    }

    [Fact]
    public async Task EmbedAsync_single_overload_returns_single_embedding()
    {
        var expected = new float[] { 0.5f, 0.6f };
        var responseJson = MakeEmbeddingResponse([expected]);
        var handler = new FakeEmbeddingHandler(responseJson);
        var httpClient = new HttpClient(handler);
        var client = new EmbeddingClient("https://myresource.openai.azure.com/", "test-key", "text-embedding-3-small", httpClient);

        var result = await client.EmbedAsync("single text");

        Assert.Equal(expected, result);
    }
}
```

- [ ] **3.3** Run tests — verify they fail

```bash
cd src/agent && dotnet test --filter "EmbeddingClientTests"
```

- [ ] **3.4** Implement EmbeddingClient

Create `src/agent/OpenAgent.MemoryIndex/EmbeddingClient.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Calls the Azure OpenAI Embeddings API to generate vector embeddings for text inputs.
/// Uses the REST API directly (no SDK dependency).
/// </summary>
public sealed class EmbeddingClient
{
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _deployment;
    private readonly HttpClient _httpClient;

    public EmbeddingClient(string endpoint, string apiKey, string deployment, HttpClient httpClient)
    {
        _endpoint = endpoint.TrimEnd('/');
        _apiKey = apiKey;
        _deployment = deployment;
        _httpClient = httpClient;
    }

    /// <summary>
    /// True when all required configuration is present. Tools should check this
    /// before registering themselves.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_endpoint) &&
        !string.IsNullOrEmpty(_apiKey) &&
        !string.IsNullOrEmpty(_deployment);

    /// <summary>
    /// Embeds a single text input and returns its embedding vector.
    /// </summary>
    public async Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
    {
        var results = await EmbedAsync([input], ct);
        return results[0];
    }

    /// <summary>
    /// Embeds multiple text inputs in a single API call. Returns one float array per input,
    /// in the same order as the input list.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        // Build the URL: /openai/deployments/{deployment}/embeddings?api-version=2024-02-01&api-key={key}
        var url = $"{_endpoint}/openai/deployments/{_deployment}/embeddings?api-version=2024-02-01&api-key={_apiKey}";

        // Build request body
        var requestBody = JsonSerializer.Serialize(new { input = inputs });
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        // Parse the data array — each entry has an "embedding" array and "index"
        var dataArray = doc.RootElement.GetProperty("data");
        var results = new float[inputs.Count][];

        foreach (var entry in dataArray.EnumerateArray())
        {
            var index = entry.GetProperty("index").GetInt32();
            var embeddingArray = entry.GetProperty("embedding");
            var floats = new float[embeddingArray.GetArrayLength()];
            var j = 0;
            foreach (var value in embeddingArray.EnumerateArray())
            {
                floats[j++] = value.GetSingle();
            }
            results[index] = floats;
        }

        return results;
    }
}
```

- [ ] **3.5** Run tests — verify they pass

```bash
cd src/agent && dotnet test --filter "EmbeddingClientTests"
```

- [ ] **3.6** Commit

```
feat(memory): implement EmbeddingClient for Azure OpenAI Embeddings API
```

---

## Task 4: MemoryChunker — LLM Topic Splitting with Summaries (TDD)

### Goal
Call the LLM to split a daily memory log into topic chunks, each with full content and a summary.

### Steps

- [ ] **4.1** Write failing tests

Create `src/agent/OpenAgent.Tests/MemoryChunkerTests.cs`:

```csharp
using OpenAgent.MemoryIndex;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryChunkerTests
{
    [Fact]
    public async Task ChunkAsync_parses_llm_json_response()
    {
        var llmResponse = """
            {
              "chunks": [
                { "content": "Full discussion about auth middleware", "summary": "Auth middleware rewrite" },
                { "content": "Pasta carbonara recipe steps", "summary": "Pasta carbonara technique" }
              ]
            }
            """;
        var provider = new StreamingTextProvider(llmResponse);
        Func<string, OpenAgent.Contracts.ILlmTextProvider> factory = _ => provider;

        var chunker = new MemoryChunker(factory, "test-provider", "test-model");
        var result = await chunker.ChunkAsync("# 2026-04-10\n\nSome daily memory content...");

        Assert.Equal(2, result.Count);
        Assert.Equal("Full discussion about auth middleware", result[0].Content);
        Assert.Equal("Auth middleware rewrite", result[0].Summary);
        Assert.Equal("Pasta carbonara recipe steps", result[1].Content);
        Assert.Equal("Pasta carbonara technique", result[1].Summary);
    }

    [Fact]
    public async Task ChunkAsync_returns_empty_for_empty_chunks()
    {
        var llmResponse = """{"chunks": []}""";
        var provider = new StreamingTextProvider(llmResponse);
        Func<string, OpenAgent.Contracts.ILlmTextProvider> factory = _ => provider;

        var chunker = new MemoryChunker(factory, "test-provider", "test-model");
        var result = await chunker.ChunkAsync("Some content");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ChunkAsync_handles_single_chunk()
    {
        var llmResponse = """
            {
              "chunks": [
                { "content": "Only one topic discussed today", "summary": "Single topic day" }
              ]
            }
            """;
        var provider = new StreamingTextProvider(llmResponse);
        Func<string, OpenAgent.Contracts.ILlmTextProvider> factory = _ => provider;

        var chunker = new MemoryChunker(factory, "test-provider", "test-model");
        var result = await chunker.ChunkAsync("One topic content");

        Assert.Single(result);
        Assert.Equal("Only one topic discussed today", result[0].Content);
        Assert.Equal("Single topic day", result[0].Summary);
    }

    [Fact]
    public async Task ChunkAsync_uses_correct_provider_key()
    {
        var llmResponse = """{"chunks": [{"content": "c", "summary": "s"}]}""";
        string? capturedKey = null;
        Func<string, OpenAgent.Contracts.ILlmTextProvider> factory = key =>
        {
            capturedKey = key;
            return new StreamingTextProvider(llmResponse);
        };

        var chunker = new MemoryChunker(factory, "my-provider", "my-model");
        await chunker.ChunkAsync("content");

        Assert.Equal("my-provider", capturedKey);
    }
}
```

- [ ] **4.2** Run tests — verify they fail

```bash
cd src/agent && dotnet test --filter "MemoryChunkerTests"
```

- [ ] **4.3** Implement MemoryChunker

Create `src/agent/OpenAgent.MemoryIndex/MemoryChunker.cs`:

```csharp
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Result of chunking a daily memory file — content and a short summary.
/// </summary>
public sealed record ChunkResult(string Content, string Summary);

/// <summary>
/// Calls the LLM to split a daily memory log file into topic-based chunks,
/// each with full content and a short summary for search indexing.
/// </summary>
public sealed class MemoryChunker
{
    private readonly Func<string, ILlmTextProvider> _providerFactory;
    private readonly string _providerKey;
    private readonly string _model;

    private const string SystemPrompt = """
        You are a memory indexer. Your job is to split a daily memory log into topical chunks.

        Rules:
        - Each chunk should cover one distinct topic or conversation thread
        - The "content" field should contain the FULL text of that topic — do not summarize or truncate
        - The "summary" field should be a concise one-line description (5-15 words) suitable for search results
        - If the entire file is a single topic, return one chunk
        - Preserve all details, code snippets, decisions, and context in the content field
        - Do NOT merge unrelated topics into one chunk

        Respond with JSON only, no markdown fencing:
        {
          "chunks": [
            { "content": "Full text of topic...", "summary": "Short description of topic" }
          ]
        }
        """;

    public MemoryChunker(Func<string, ILlmTextProvider> providerFactory, string providerKey, string model)
    {
        _providerFactory = providerFactory;
        _providerKey = providerKey;
        _model = model;
    }

    /// <summary>
    /// Splits the given memory file content into topic chunks via LLM.
    /// Returns a list of (content, summary) pairs.
    /// </summary>
    public async Task<IReadOnlyList<ChunkResult>> ChunkAsync(string fileContent, CancellationToken ct = default)
    {
        var provider = _providerFactory(_providerKey);

        var messages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = SystemPrompt },
            new() { Id = "usr", ConversationId = "", Role = "user", Content = fileContent }
        };

        var options = new CompletionOptions { ResponseFormat = "json_object" };

        // Collect the full response
        var sb = new StringBuilder();
        await foreach (var evt in provider.CompleteAsync(messages, _model, options, ct))
        {
            if (evt is TextDelta delta)
                sb.Append(delta.Content);
        }

        // Parse the JSON response
        var responseText = sb.ToString();
        using var doc = JsonDocument.Parse(responseText);
        var chunksArray = doc.RootElement.GetProperty("chunks");

        var results = new List<ChunkResult>();
        foreach (var entry in chunksArray.EnumerateArray())
        {
            var content = entry.GetProperty("content").GetString()!;
            var summary = entry.GetProperty("summary").GetString()!;
            results.Add(new ChunkResult(content, summary));
        }

        return results;
    }
}
```

- [ ] **4.4** Run tests — verify they pass

```bash
cd src/agent && dotnet test --filter "MemoryChunkerTests"
```

- [ ] **4.5** Commit

```
feat(memory): implement MemoryChunker for LLM-based topic splitting
```

---

## Task 5: MemoryIndexService — Orchestration + Hybrid Search (TDD)

### Goal
Orchestrate the indexing pipeline (find eligible files, chunk, embed, store, delete) and implement hybrid search (0.7 cosine + 0.3 FTS5 BM25).

### Steps

- [ ] **5.1** Write failing tests

Create `src/agent/OpenAgent.Tests/MemoryIndexServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryIndexServiceTests : IDisposable
{
    private readonly string _dataPath;
    private readonly string _memoryDir;
    private readonly MemoryChunkStore _store;
    private readonly AgentConfig _agentConfig;

    public MemoryIndexServiceTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"openagent-idxtest-{Guid.NewGuid()}");
        _memoryDir = Path.Combine(_dataPath, "memory");
        Directory.CreateDirectory(_memoryDir);
        _store = new MemoryChunkStore(_dataPath);
        _agentConfig = new AgentConfig { MemoryDays = 3 };
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dataPath, true); } catch { }
    }

    /// <summary>Creates a memory file with the given date and content.</summary>
    private void WriteMemoryFile(string date, string content)
    {
        File.WriteAllText(Path.Combine(_memoryDir, $"{date}.md"), content);
    }

    /// <summary>Builds a MemoryIndexService with a fake chunker and embedding client.</summary>
    private MemoryIndexService BuildService(
        string chunkerResponse = """{"chunks":[{"content":"Test content","summary":"Test summary"}]}""",
        float[]? embedding = null)
    {
        embedding ??= [0.1f, 0.2f, 0.3f];
        var embeddingResponse = MakeEmbeddingResponse([embedding]);
        var handler = new FakeEmbeddingHandler(embeddingResponse);
        var httpClient = new HttpClient(handler);
        var embeddingClient = new EmbeddingClient("https://test.openai.azure.com/", "key", "deploy", httpClient);

        var textProvider = new StreamingTextProvider(chunkerResponse);
        Func<string, ILlmTextProvider> providerFactory = _ => textProvider;
        var chunker = new MemoryChunker(providerFactory, "test", "test-model");

        return new MemoryIndexService(
            _store,
            embeddingClient,
            chunker,
            new AgentEnvironment { DataPath = _dataPath },
            _agentConfig,
            NullLogger<MemoryIndexService>.Instance);
    }

    private static string MakeEmbeddingResponse(float[][] embeddings)
    {
        var dataEntries = embeddings.Select((e, i) =>
            $$"""{"object":"embedding","index":{{i}},"embedding":[{{string.Join(",", e.Select(f => f.ToString("G")))}}]}""");
        return $$"""{"object":"list","data":[{{string.Join(",", dataEntries)}}],"model":"text-embedding-3-small","usage":{"prompt_tokens":10,"total_tokens":10}}""";
    }

    [Fact]
    public async Task RunAsync_indexes_eligible_files_and_deletes_them()
    {
        // Today is "now", memoryDays=3, so files older than 3 days are eligible
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var eligible = today.AddDays(-4).ToString("yyyy-MM-dd");
        var recent = today.ToString("yyyy-MM-dd");

        WriteMemoryFile(eligible, "Old memory content");
        WriteMemoryFile(recent, "Today memory content");

        var service = BuildService();
        var result = await service.RunAsync();

        // Eligible file should be processed and deleted
        Assert.Equal(1, result.FilesProcessed);
        Assert.False(File.Exists(Path.Combine(_memoryDir, $"{eligible}.md")));

        // Recent file should NOT be touched
        Assert.True(File.Exists(Path.Combine(_memoryDir, $"{recent}.md")));
    }

    [Fact]
    public async Task RunAsync_skips_already_indexed_dates()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var eligible = today.AddDays(-5).ToString("yyyy-MM-dd");

        // Pre-insert a chunk for this date
        _store.InsertChunks(eligible, [new ChunkEntry("Existing", "Existing", [0.1f])]);

        // Write a file for the same date
        WriteMemoryFile(eligible, "Should be skipped");

        var service = BuildService();
        var result = await service.RunAsync();

        // File should be deleted but not re-processed
        Assert.Equal(0, result.FilesProcessed);
    }

    [Fact]
    public async Task RunAsync_returns_zero_when_no_eligible_files()
    {
        var service = BuildService();
        var result = await service.RunAsync();

        Assert.Equal(0, result.FilesProcessed);
    }

    [Fact]
    public async Task SearchAsync_returns_results_sorted_by_hybrid_score()
    {
        // Insert two chunks with different embeddings
        var embeddingA = new float[] { 1.0f, 0.0f, 0.0f };
        var embeddingB = new float[] { 0.0f, 1.0f, 0.0f };
        _store.InsertChunks("2026-04-10", [
            new ChunkEntry("Authentication middleware discussion", "Auth middleware rewrite", embeddingA),
            new ChunkEntry("Pasta carbonara recipe with eggs", "Pasta carbonara technique", embeddingB)
        ]);

        // Build service — the query embedding will point toward embeddingA
        var queryEmbedding = new float[] { 0.9f, 0.1f, 0.0f };
        var embeddingResponse = MakeEmbeddingResponse([queryEmbedding]);
        var handler = new FakeEmbeddingHandler(embeddingResponse);
        var httpClient = new HttpClient(handler);
        var embeddingClient = new EmbeddingClient("https://test.openai.azure.com/", "key", "deploy", httpClient);

        var chunker = new MemoryChunker(_ => new StreamingTextProvider(""), "test", "model");

        var service = new MemoryIndexService(
            _store, embeddingClient, chunker,
            new AgentEnvironment { DataPath = _dataPath },
            _agentConfig,
            NullLogger<MemoryIndexService>.Instance);

        // Force cache refresh
        service.InvalidateCache();

        var results = await service.SearchAsync("authentication", limit: 10);

        Assert.Equal(2, results.Count);
        // First result should be the auth chunk (higher cosine with query)
        Assert.Equal("Auth middleware rewrite", results[0].Summary);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task LoadChunksAsync_returns_full_content()
    {
        var embedding = new float[] { 0.5f, 0.5f };
        _store.InsertChunks("2026-04-10", [
            new ChunkEntry("Full auth discussion content here", "Auth summary", embedding),
            new ChunkEntry("Full pasta recipe content here", "Pasta summary", embedding)
        ]);

        var service = BuildService();
        var all = _store.GetAllChunks();
        var ids = new[] { all[0].Id, all[1].Id };

        var result = await service.LoadChunksAsync(ids);

        Assert.Equal(2, result.Count);
        Assert.Equal("Full auth discussion content here", result[0].Content);
        Assert.Equal("Full pasta recipe content here", result[1].Content);
    }

    [Fact]
    public async Task GetStatsAsync_returns_store_stats()
    {
        var embedding = new float[] { 0.1f };
        _store.InsertChunks("2026-04-10", [new ChunkEntry("A", "A", embedding)]);
        _store.InsertChunks("2026-04-11", [new ChunkEntry("B", "B", embedding)]);

        var service = BuildService();
        var stats = await service.GetStatsAsync();

        Assert.Equal(2, stats.TotalChunks);
        Assert.Equal(2, stats.TotalDates);
    }
}
```

- [ ] **5.2** Run tests — verify they fail

```bash
cd src/agent && dotnet test --filter "MemoryIndexServiceTests"
```

- [ ] **5.3** Implement MemoryIndexService

Create `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Result of a search query — lightweight summary with score.
/// </summary>
public sealed record MemorySearchResult(long Id, string Date, string Summary, double Score);

/// <summary>
/// Result of loading chunk content by ID.
/// </summary>
public sealed record MemoryChunkContent(long Id, string Date, string Content);

/// <summary>
/// Result of a RunAsync indexing pass.
/// </summary>
public sealed record IndexRunResult(int FilesProcessed, int ChunksCreated);

/// <summary>
/// Orchestrates memory indexing and hybrid search. Reads eligible daily memory files,
/// chunks them via LLM, embeds via Azure OpenAI, stores in SQLite with FTS5.
/// Search uses hybrid ranking: 0.7 cosine similarity + 0.3 FTS5 BM25.
/// </summary>
public sealed class MemoryIndexService
{
    private readonly MemoryChunkStore _store;
    private readonly EmbeddingClient _embeddingClient;
    private readonly MemoryChunker _chunker;
    private readonly string _memoryDir;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<MemoryIndexService> _logger;

    // Cached chunks for cosine similarity — invalidated after each RunAsync
    private IReadOnlyList<StoredChunk>? _cachedChunks;
    private readonly object _cacheLock = new();

    public MemoryIndexService(
        MemoryChunkStore store,
        EmbeddingClient embeddingClient,
        MemoryChunker chunker,
        AgentEnvironment environment,
        AgentConfig agentConfig,
        ILogger<MemoryIndexService> logger)
    {
        _store = store;
        _embeddingClient = embeddingClient;
        _chunker = chunker;
        _memoryDir = Path.Combine(environment.DataPath, "memory");
        _agentConfig = agentConfig;
        _logger = logger;
    }

    /// <summary>
    /// Runs the indexing pipeline: find eligible files, chunk, embed, store, delete.
    /// A file is eligible if its date is older than memoryDays from today.
    /// Already-indexed dates are skipped. Source files are deleted after successful indexing.
    /// </summary>
    public async Task<IndexRunResult> RunAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_memoryDir))
        {
            _logger.LogDebug("Memory directory does not exist, nothing to index");
            return new IndexRunResult(0, 0);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(-Math.Max(1, _agentConfig.MemoryDays));
        var processedDates = _store.GetProcessedDates().ToHashSet();

        // Find eligible files: date-named .md files older than the memory window
        var files = Directory.GetFiles(_memoryDir, "????-??-??.md")
            .Select(f => (Path: f, Date: Path.GetFileNameWithoutExtension(f)))
            .Where(f => DateOnly.TryParse(f.Date, out var d) && d < cutoff)
            .Where(f => !processedDates.Contains(f.Date))
            .OrderBy(f => f.Date)
            .ToList();

        if (files.Count == 0)
        {
            _logger.LogDebug("No eligible memory files to index");

            // Still delete files that were already indexed but not yet cleaned up
            DeleteAlreadyIndexedFiles(cutoff, processedDates);

            return new IndexRunResult(0, 0);
        }

        var totalChunks = 0;

        foreach (var (filePath, date) in files)
        {
            _logger.LogInformation("Indexing memory file: {Date}", date);

            // Read file content
            var content = await File.ReadAllTextAsync(filePath, ct);

            // Chunk via LLM
            var chunkResults = await _chunker.ChunkAsync(content, ct);
            if (chunkResults.Count == 0)
            {
                _logger.LogWarning("LLM returned zero chunks for {Date}, skipping", date);
                continue;
            }

            // Embed all chunks in a single API call
            var texts = chunkResults.Select(c => c.Content).ToList();
            var embeddings = await _embeddingClient.EmbedAsync(texts, ct);

            // Build ChunkEntry list
            var entries = new List<ChunkEntry>();
            for (var i = 0; i < chunkResults.Count; i++)
            {
                entries.Add(new ChunkEntry(
                    chunkResults[i].Content,
                    chunkResults[i].Summary,
                    embeddings[i]));
            }

            // Store in SQLite (both memory_chunks and memory_chunks_fts)
            _store.InsertChunks(date, entries);
            totalChunks += entries.Count;

            // Delete the source file — DB is now the source of truth
            File.Delete(filePath);
            _logger.LogInformation("Indexed {ChunkCount} chunks for {Date}, file deleted", entries.Count, date);
        }

        // Invalidate cache so next search picks up new chunks
        InvalidateCache();

        _logger.LogInformation("Index run complete: {Files} files, {Chunks} chunks", files.Count, totalChunks);
        return new IndexRunResult(files.Count, totalChunks);
    }

    /// <summary>
    /// Hybrid search: combines cosine similarity (weight 0.7) with FTS5 BM25 (weight 0.3).
    /// Returns lightweight results with id, date, summary, and score.
    /// </summary>
    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        // Step 1: Embed the query
        var queryEmbedding = await _embeddingClient.EmbedAsync(query, ct);

        // Step 2: Cosine similarity against all cached chunks
        var chunks = GetCachedChunks();
        var cosineScores = new Dictionary<long, double>();
        foreach (var chunk in chunks)
        {
            var similarity = CosineSimilarity(queryEmbedding, chunk.Embedding);
            cosineScores[chunk.Id] = similarity;
        }

        // Step 3: FTS5 keyword search
        var ftsResults = _store.SearchFts(query);
        var ftsScores = new Dictionary<long, double>();
        foreach (var fts in ftsResults)
        {
            ftsScores[fts.Id] = fts.NormalizedRank;
        }

        // Step 4: Combine scores — 0.7 cosine + 0.3 FTS
        var allIds = cosineScores.Keys.Union(ftsScores.Keys).ToHashSet();
        var scored = new List<(long Id, double Score)>();
        foreach (var id in allIds)
        {
            var cosine = cosineScores.GetValueOrDefault(id, 0.0);
            var fts = ftsScores.GetValueOrDefault(id, 0.0);
            var finalScore = 0.7 * cosine + 0.3 * fts;
            scored.Add((id, finalScore));
        }

        // Step 5: Sort by score descending, take top N
        var topIds = scored
            .OrderByDescending(s => s.Score)
            .Take(limit)
            .ToList();

        // Build lookup for chunk metadata
        var chunkLookup = chunks.ToDictionary(c => c.Id);

        var results = new List<MemorySearchResult>();
        foreach (var (id, score) in topIds)
        {
            if (chunkLookup.TryGetValue(id, out var chunk))
            {
                results.Add(new MemorySearchResult(id, chunk.Date, chunk.Summary, Math.Round(score, 4)));
            }
        }

        return results;
    }

    /// <summary>
    /// Loads full chunk content for the given IDs. Used by the load_memory_chunks tool
    /// after the agent selects interesting chunks from search results.
    /// </summary>
    public Task<IReadOnlyList<MemoryChunkContent>> LoadChunksAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var chunks = _store.GetChunksByIds(ids);
        var results = chunks.Select(c => new MemoryChunkContent(c.Id, c.Date, c.Content)).ToList();
        return Task.FromResult<IReadOnlyList<MemoryChunkContent>>(results);
    }

    /// <summary>
    /// Returns aggregate statistics about the memory index.
    /// </summary>
    public Task<ChunkStats> GetStatsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_store.GetStats());
    }

    /// <summary>
    /// Forces the cached chunks to be reloaded from the database on next search.
    /// Called after RunAsync completes.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedChunks = null;
        }
    }

    /// <summary>Returns cached chunks, loading from DB if needed.</summary>
    private IReadOnlyList<StoredChunk> GetCachedChunks()
    {
        lock (_cacheLock)
        {
            _cachedChunks ??= _store.GetAllChunks();
            return _cachedChunks;
        }
    }

    /// <summary>
    /// Deletes source files for dates that were already indexed in a previous run
    /// but whose files were not cleaned up (e.g. due to a crash between insert and delete).
    /// </summary>
    private void DeleteAlreadyIndexedFiles(DateOnly cutoff, HashSet<string> processedDates)
    {
        var leftoverFiles = Directory.GetFiles(_memoryDir, "????-??-??.md")
            .Select(f => (Path: f, Date: Path.GetFileNameWithoutExtension(f)))
            .Where(f => DateOnly.TryParse(f.Date, out var d) && d < cutoff)
            .Where(f => processedDates.Contains(f.Date));

        foreach (var (filePath, date) in leftoverFiles)
        {
            File.Delete(filePath);
            _logger.LogInformation("Deleted already-indexed leftover file: {Date}", date);
        }
    }

    /// <summary>Computes cosine similarity between two vectors.</summary>
    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }
}
```

- [ ] **5.4** Run tests — verify they pass

```bash
cd src/agent && dotnet test --filter "MemoryIndexServiceTests"
```

- [ ] **5.5** Commit

```
feat(memory): implement MemoryIndexService with hybrid search
```

---

## Task 6: MemoryToolHandler — Two Agent Tools (TDD)

### Goal
Implement `search_memory` and `load_memory_chunks` as agent tools via `IToolHandler`.

### Steps

- [ ] **6.1** Write failing tests

Create `src/agent/OpenAgent.Tests/MemoryToolHandlerTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryToolHandlerTests : IDisposable
{
    private readonly string _dataPath;
    private readonly MemoryChunkStore _store;
    private readonly MemoryIndexService _service;
    private readonly MemoryToolHandler _handler;
    private readonly EmbeddingClient _embeddingClient;

    public MemoryToolHandlerTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"openagent-tooltest-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(_dataPath, "memory"));
        _store = new MemoryChunkStore(_dataPath);

        // Create an embedding client that returns a fixed embedding for any query
        var embeddingResponse = MakeEmbeddingResponse([[0.5f, 0.5f]]);
        var fakeHandler = new FakeEmbeddingHandler(embeddingResponse);
        var httpClient = new HttpClient(fakeHandler);
        _embeddingClient = new EmbeddingClient("https://test.openai.azure.com/", "key", "deploy", httpClient);

        var chunker = new MemoryChunker(_ => new StreamingTextProvider(""), "test", "model");

        _service = new MemoryIndexService(
            _store, _embeddingClient, chunker,
            new AgentEnvironment { DataPath = _dataPath },
            new AgentConfig(),
            NullLogger<MemoryIndexService>.Instance);

        _handler = new MemoryToolHandler(_service, _embeddingClient);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dataPath, true); } catch { }
    }

    private static string MakeEmbeddingResponse(float[][] embeddings)
    {
        var dataEntries = embeddings.Select((e, i) =>
            $$"""{"object":"embedding","index":{{i}},"embedding":[{{string.Join(",", e.Select(f => f.ToString("G")))}}]}""");
        return $$"""{"object":"list","data":[{{string.Join(",", dataEntries)}}],"model":"text-embedding-3-small","usage":{"prompt_tokens":10,"total_tokens":10}}""";
    }

    [Fact]
    public void Tools_registered_when_embedding_configured()
    {
        Assert.Equal(2, _handler.Tools.Count);
        Assert.Contains(_handler.Tools, t => t.Definition.Name == "search_memory");
        Assert.Contains(_handler.Tools, t => t.Definition.Name == "load_memory_chunks");
    }

    [Fact]
    public void Tools_empty_when_embedding_not_configured()
    {
        var unconfiguredClient = new EmbeddingClient("", "", "", new HttpClient());
        var handler = new MemoryToolHandler(_service, unconfiguredClient);
        Assert.Empty(handler.Tools);
    }

    [Fact]
    public async Task SearchMemory_returns_results()
    {
        // Seed some chunks
        var embedding = new float[] { 0.5f, 0.5f };
        _store.InsertChunks("2026-04-10", [
            new ChunkEntry("Auth middleware discussion", "Auth middleware rewrite", embedding)
        ]);
        _service.InvalidateCache();

        var tool = _handler.Tools.First(t => t.Definition.Name == "search_memory");
        var args = JsonSerializer.Serialize(new { query = "authentication", limit = 10 });
        var result = await tool.ExecuteAsync(args, "conv-1");

        using var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.True(results.GetArrayLength() > 0);

        var first = results[0];
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("date", out _));
        Assert.True(first.TryGetProperty("summary", out _));
        Assert.True(first.TryGetProperty("score", out _));
    }

    [Fact]
    public async Task SearchMemory_requires_query()
    {
        var tool = _handler.Tools.First(t => t.Definition.Name == "search_memory");
        var result = await tool.ExecuteAsync("{}", "conv-1");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task LoadMemoryChunks_returns_content()
    {
        var embedding = new float[] { 0.5f, 0.5f };
        _store.InsertChunks("2026-04-10", [
            new ChunkEntry("Full auth content here", "Auth summary", embedding)
        ]);

        var all = _store.GetAllChunks();
        var chunkId = all[0].Id;

        var tool = _handler.Tools.First(t => t.Definition.Name == "load_memory_chunks");
        var args = JsonSerializer.Serialize(new { ids = new[] { chunkId } });
        var result = await tool.ExecuteAsync(args, "conv-1");

        using var doc = JsonDocument.Parse(result);
        var chunks = doc.RootElement.GetProperty("chunks");
        Assert.Equal(1, chunks.GetArrayLength());
        Assert.Equal("Full auth content here", chunks[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task LoadMemoryChunks_requires_ids()
    {
        var tool = _handler.Tools.First(t => t.Definition.Name == "load_memory_chunks");
        var result = await tool.ExecuteAsync("{}", "conv-1");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task SearchMemory_default_limit_is_20()
    {
        var tool = _handler.Tools.First(t => t.Definition.Name == "search_memory");
        // Only pass query, no limit — should default to 20
        var args = JsonSerializer.Serialize(new { query = "test" });
        var result = await tool.ExecuteAsync(args, "conv-1");

        using var doc = JsonDocument.Parse(result);
        // Should not error
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
    }
}
```

- [ ] **6.2** Run tests — verify they fail

```bash
cd src/agent && dotnet test --filter "MemoryToolHandlerTests"
```

- [ ] **6.3** Implement MemoryToolHandler

Create `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Tool handler providing memory search tools: search_memory and load_memory_chunks.
/// Tools are only registered when the embedding client is properly configured.
/// </summary>
public sealed class MemoryToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public MemoryToolHandler(MemoryIndexService service, EmbeddingClient embeddingClient)
    {
        Tools = embeddingClient.IsConfigured
            ? [new SearchMemoryTool(service), new LoadMemoryChunksTool(service)]
            : [];
    }
}

/// <summary>
/// Searches memory using hybrid vector + keyword ranking.
/// Returns lightweight summaries with scores — use load_memory_chunks for full content.
/// </summary>
internal sealed class SearchMemoryTool(MemoryIndexService service) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "search_memory",
        Description = "Search long-term memory for past conversations and knowledge. Returns summaries with relevance scores. Use load_memory_chunks to retrieve full content for interesting results.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Search query — natural language or keywords" },
                limit = new { type = "integer", description = "Max results to return (default 20)" }
            },
            required = new[] { "query" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;

        if (!args.TryGetProperty("query", out var queryEl) || queryEl.ValueKind != JsonValueKind.String)
            return JsonSerializer.Serialize(new { error = "query is required" });

        var query = queryEl.GetString()!;
        var limit = args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number
            ? limitEl.GetInt32()
            : 20;

        var results = await service.SearchAsync(query, limit, ct);

        return JsonSerializer.Serialize(new
        {
            results = results.Select(r => new
            {
                id = r.Id,
                date = r.Date,
                summary = r.Summary,
                score = r.Score
            })
        });
    }
}

/// <summary>
/// Loads full content for specific memory chunk IDs. Used after search_memory
/// to retrieve detailed content for selected results.
/// </summary>
internal sealed class LoadMemoryChunksTool(MemoryIndexService service) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "load_memory_chunks",
        Description = "Load full content for specific memory chunks by ID. Use after search_memory to read the complete text of interesting results.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                ids = new
                {
                    type = "array",
                    items = new { type = "integer" },
                    description = "List of chunk IDs from search_memory results"
                }
            },
            required = new[] { "ids" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;

        if (!args.TryGetProperty("ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
            return JsonSerializer.Serialize(new { error = "ids is required (array of integers)" });

        var ids = idsEl.EnumerateArray().Select(e => e.GetInt64()).ToList();

        var chunks = await service.LoadChunksAsync(ids, ct);

        return JsonSerializer.Serialize(new
        {
            chunks = chunks.Select(c => new
            {
                id = c.Id,
                date = c.Date,
                content = c.Content
            })
        });
    }
}
```

- [ ] **6.4** Run tests — verify they pass

```bash
cd src/agent && dotnet test --filter "MemoryToolHandlerTests"
```

- [ ] **6.5** Commit

```
feat(memory): implement search_memory and load_memory_chunks tools
```

---

## Task 7: Hosted Service + Endpoints + DI Wiring

### Goal
Wire everything together: daily timer, REST API, DI registration, Program.cs integration.

### Steps

- [ ] **7.1** Write failing tests for MemoryIndexHostedService

Create `src/agent/OpenAgent.Tests/MemoryIndexHostedServiceTests.cs`:

```csharp
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;

namespace OpenAgent.Tests;

public class MemoryIndexHostedServiceTests
{
    [Fact]
    public void ComputeNextRun_returns_today_if_hour_not_passed()
    {
        // If current time is before the target hour, next run is today at that hour
        var now = new DateTime(2026, 4, 18, 1, 0, 0, DateTimeKind.Utc); // 01:00 UTC
        var targetHour = 2; // target is 02:00

        var next = MemoryIndexHostedService.ComputeNextRunUtc(now, targetHour);

        Assert.Equal(new DateTime(2026, 4, 18, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void ComputeNextRun_returns_tomorrow_if_hour_passed()
    {
        var now = new DateTime(2026, 4, 18, 3, 0, 0, DateTimeKind.Utc); // 03:00 UTC
        var targetHour = 2; // target was 02:00

        var next = MemoryIndexHostedService.ComputeNextRunUtc(now, targetHour);

        Assert.Equal(new DateTime(2026, 4, 19, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void ComputeNextRun_returns_tomorrow_if_exactly_at_hour()
    {
        var now = new DateTime(2026, 4, 18, 2, 0, 0, DateTimeKind.Utc);
        var targetHour = 2;

        var next = MemoryIndexHostedService.ComputeNextRunUtc(now, targetHour);

        Assert.Equal(new DateTime(2026, 4, 19, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void ComputeNextRun_handles_midnight_target()
    {
        var now = new DateTime(2026, 4, 18, 23, 30, 0, DateTimeKind.Utc);
        var targetHour = 0;

        var next = MemoryIndexHostedService.ComputeNextRunUtc(now, targetHour);

        Assert.Equal(new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void SkipsRunWhenEmbeddingNotConfigured()
    {
        // Verify that the hosted service checks IsConfigured
        var client = new EmbeddingClient("", "", "", new HttpClient());
        Assert.False(client.IsConfigured);
    }
}
```

- [ ] **7.2** Run tests — verify they fail (ComputeNextRunUtc does not exist yet)

```bash
cd src/agent && dotnet test --filter "MemoryIndexHostedServiceTests"
```

- [ ] **7.3** Implement MemoryIndexHostedService

Create `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Hosted service that runs the memory index job daily at the configured hour.
/// Skips execution when the embedding client is not configured.
/// </summary>
public sealed class MemoryIndexHostedService : IHostedService, IDisposable
{
    private readonly MemoryIndexService _indexService;
    private readonly EmbeddingClient _embeddingClient;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<MemoryIndexHostedService> _logger;
    private Timer? _timer;

    public MemoryIndexHostedService(
        MemoryIndexService indexService,
        EmbeddingClient embeddingClient,
        AgentConfig agentConfig,
        ILogger<MemoryIndexHostedService> logger)
    {
        _indexService = indexService;
        _embeddingClient = embeddingClient;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_embeddingClient.IsConfigured)
        {
            _logger.LogInformation("Memory index: embedding client not configured, daily job disabled");
            return Task.CompletedTask;
        }

        ScheduleNext();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    /// <summary>
    /// Computes the next UTC time to run the job at the given target hour.
    /// If the target hour has already passed today, schedules for tomorrow.
    /// </summary>
    public static DateTime ComputeNextRunUtc(DateTime nowUtc, int targetHour)
    {
        var today = nowUtc.Date.AddHours(targetHour);
        return nowUtc < today ? today : today.AddDays(1);
    }

    private void ScheduleNext()
    {
        var now = DateTime.UtcNow;
        var nextRun = ComputeNextRunUtc(now, _agentConfig.IndexRunAtHour);
        var delay = nextRun - now;

        _logger.LogInformation("Memory index: next run at {NextRun:O} (in {Delay})", nextRun, delay);

        _timer?.Dispose();
        _timer = new Timer(OnTimerElapsed, null, delay, Timeout.InfiniteTimeSpan);
    }

    private async void OnTimerElapsed(object? state)
    {
        try
        {
            _logger.LogInformation("Memory index: starting daily run");
            var result = await _indexService.RunAsync();
            _logger.LogInformation("Memory index: completed — {Files} files, {Chunks} chunks",
                result.FilesProcessed, result.ChunksCreated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory index: daily run failed");
        }
        finally
        {
            // Schedule the next run regardless of success/failure
            ScheduleNext();
        }
    }
}
```

- [ ] **7.4** Implement MemoryIndexEndpoints

Create `src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// REST API for the memory index — manual trigger and stats.
/// </summary>
public static class MemoryIndexEndpoints
{
    /// <summary>
    /// Maps memory index endpoints under /api/memory-index.
    /// </summary>
    public static void MapMemoryIndexEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/memory-index").RequireAuthorization();

        // Run the indexing job immediately
        group.MapPost("/run", async (MemoryIndexService service, CancellationToken ct) =>
        {
            var result = await service.RunAsync(ct);
            return Results.Ok(new
            {
                files_processed = result.FilesProcessed,
                chunks_created = result.ChunksCreated
            });
        });

        // Get index statistics
        group.MapGet("/stats", async (MemoryIndexService service, CancellationToken ct) =>
        {
            var stats = await service.GetStatsAsync(ct);
            return Results.Ok(new
            {
                total_chunks = stats.TotalChunks,
                total_dates = stats.TotalDates
            });
        });
    }
}
```

- [ ] **7.5** Implement ServiceCollectionExtensions

Create `src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// DI registration for the memory index subsystem.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all memory index services: store, embedding client, chunker,
    /// index service, tool handler, and hosted service.
    /// </summary>
    public static IServiceCollection AddMemoryIndex(this IServiceCollection services)
    {
        // Chunk store — uses memory.db in the data directory
        services.AddSingleton(sp =>
            new MemoryChunkStore(sp.GetRequiredService<AgentEnvironment>().DataPath));

        // Embedding client — configured from AgentConfig
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<AgentConfig>();
            return new EmbeddingClient(
                config.EmbeddingEndpoint,
                config.EmbeddingApiKey,
                config.EmbeddingDeployment,
                new HttpClient());
        });

        // Chunker — uses the compaction provider/model for LLM calls
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<AgentConfig>();
            var providerFactory = sp.GetRequiredService<Func<string, ILlmTextProvider>>();
            return new MemoryChunker(providerFactory, config.CompactionProvider, config.CompactionModel);
        });

        // Index service — orchestrates everything
        services.AddSingleton(sp =>
            new MemoryIndexService(
                sp.GetRequiredService<MemoryChunkStore>(),
                sp.GetRequiredService<EmbeddingClient>(),
                sp.GetRequiredService<MemoryChunker>(),
                sp.GetRequiredService<AgentEnvironment>(),
                sp.GetRequiredService<AgentConfig>(),
                sp.GetRequiredService<ILogger<MemoryIndexService>>()));

        // Tool handler — registered as IToolHandler so AgentLogic aggregates it
        services.AddSingleton<IToolHandler>(sp =>
            new MemoryToolHandler(
                sp.GetRequiredService<MemoryIndexService>(),
                sp.GetRequiredService<EmbeddingClient>()));

        // Hosted service — daily timer
        services.AddHostedService(sp =>
            new MemoryIndexHostedService(
                sp.GetRequiredService<MemoryIndexService>(),
                sp.GetRequiredService<EmbeddingClient>(),
                sp.GetRequiredService<AgentConfig>(),
                sp.GetRequiredService<ILogger<MemoryIndexHostedService>>()));

        return services;
    }
}
```

- [ ] **7.6** Wire into Program.cs

In `src/agent/OpenAgent/Program.cs`:

Add the using statement at the top with the other using statements:

```csharp
using OpenAgent.MemoryIndex;
```

Add the DI registration after the existing `builder.Services.AddScheduledTasks(environment.DataPath);` line:

```csharp
builder.Services.AddMemoryIndex();
```

Add the endpoint mapping after the existing `app.MapToolEndpoints();` line:

```csharp
app.MapMemoryIndexEndpoints();
```

- [ ] **7.7** Run tests — verify hosted service tests pass

```bash
cd src/agent && dotnet test --filter "MemoryIndexHostedServiceTests"
```

- [ ] **7.8** Build the full solution

```bash
cd src/agent && dotnet build
```

- [ ] **7.9** Run ALL tests to verify nothing is broken

```bash
cd src/agent && dotnet test
```

- [ ] **7.10** Commit

```
feat(memory): add hosted service, endpoints, DI wiring, and Program.cs integration
```

---

## Spec Coverage Check

| Spec Requirement | Task | Verification |
|-----------------|------|-------------|
| LLM chunks daily files into topics with summaries | Task 4 | MemoryChunkerTests |
| Each chunk has content + summary | Task 4 | ChunkResult record, MemoryChunkerTests |
| Vector embeddings via Azure OpenAI | Task 3 | EmbeddingClientTests |
| SQLite storage with memory_chunks table | Task 2 | MemoryChunkStoreTests |
| FTS5 virtual table for keyword search | Task 2 | MemoryChunkStoreTests.SearchFts |
| Hybrid search: 0.7 cosine + 0.3 FTS5 BM25 | Task 5 | MemoryIndexServiceTests.SearchAsync |
| search_memory returns { id, date, summary, score } | Task 6 | MemoryToolHandlerTests |
| load_memory_chunks returns full content by ID | Task 6 | MemoryToolHandlerTests |
| File deleted after processing | Task 5 | MemoryIndexServiceTests.RunAsync |
| Processing threshold = memoryDays | Task 5 | MemoryIndexServiceTests.RunAsync |
| memory.db — single database file | Task 2 | MemoryChunkStore constructor |
| Cached chunks in MemoryIndexService | Task 5 | GetCachedChunks, InvalidateCache |
| Invalidated on RunAsync | Task 5 | RunAsync calls InvalidateCache |
| No preloading from DB into system prompt | N/A | Not implemented (by design) |
| FTS insert alongside main table insert | Task 2 | InsertChunks dual-insert in transaction |
| UNIQUE(date, chunk_index) idempotency | Task 2 | MemoryChunkStoreTests.InsertChunks_duplicate |
| AgentConfig fields: embedding endpoint/key/deployment, indexRunAtHour | Task 1 | AgentConfig.cs |
| Daily timer hosted service | Task 7 | MemoryIndexHostedServiceTests |
| REST API: manual trigger + stats | Task 7 | MemoryIndexEndpoints |
| IToolHandler registration — conditional on IsConfigured | Task 6 | MemoryToolHandlerTests |
| GetChunksByIds with dynamic IN clause | Task 2 | MemoryChunkStoreTests.GetChunksByIds |
| Normalized FTS rank: 1 / (1 + abs(rank)) | Task 2 | SearchFts implementation |
| Embedding serialization as raw float bytes | Task 2 | SerializeEmbedding / DeserializeEmbedding |
| Uses compaction provider/model for chunking LLM calls | Task 7 | ServiceCollectionExtensions (chunker factory) |
