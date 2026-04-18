# Memory Index Job Implementation Plan (v2 — Chunk-Based)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Index closed daily memory logs into searchable topic chunks in `memory.db`, with a `search_memory` agent tool that returns matching chunk content directly.

**Architecture:** New `OpenAgent.MemoryIndex` project. Nightly job reads daily `.md` files past the memory window, LLM chunks them into topics, embeds each chunk via Azure OpenAI, stores in SQLite, deletes the source file. Agent searches via cosine similarity over cached embeddings.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, Azure OpenAI Embeddings API, xUnit

**Spec:** [docs/superpowers/specs/2026-04-16-memory-index-job-design.md](../specs/2026-04-16-memory-index-job-design.md)

**Supersedes:** [docs/superpowers/plans/2026-04-16-memory-index-job.md](2026-04-16-memory-index-job.md) (v1 — day-level summaries)

---

## File Map

### New files

| File | Responsibility |
|------|---------------|
| `src/agent/OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj` | Project file |
| `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs` | SQLite persistence for chunks, cosine similarity |
| `src/agent/OpenAgent.MemoryIndex/EmbeddingClient.cs` | Azure OpenAI Embeddings API client |
| `src/agent/OpenAgent.MemoryIndex/MemoryChunker.cs` | LLM call that splits a file into topic chunks |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs` | Orchestrates indexing and search |
| `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs` | IToolHandler: search_memory |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs` | IHostedService with daily timer |
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

---

## Task 1: Project Scaffolding + AgentConfig

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj`
- Modify: `src/agent/OpenAgent.Models/Configs/AgentConfig.cs`
- Modify: `src/agent/OpenAgent/OpenAgent.csproj`
- Modify: `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj`
- Modify: `src/agent/OpenAgent.sln`

- [ ] **Step 1: Create the project directory**

```bash
mkdir -p src/agent/OpenAgent.MemoryIndex
```

- [ ] **Step 2: Create the .csproj file**

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

- [ ] **Step 3: Add to solution and add project references**

```bash
cd src/agent && dotnet sln add OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj
```

Add to `src/agent/OpenAgent/OpenAgent.csproj` ProjectReferences:

```xml
    <ProjectReference Include="..\OpenAgent.MemoryIndex\OpenAgent.MemoryIndex.csproj" />
```

Add to `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` ProjectReferences:

```xml
    <ProjectReference Include="..\OpenAgent.MemoryIndex\OpenAgent.MemoryIndex.csproj" />
```

- [ ] **Step 4: Add AgentConfig fields**

Add to `src/agent/OpenAgent.Models/Configs/AgentConfig.cs`, before the closing `}`:

```csharp
    /// <summary>Azure OpenAI endpoint URL for embedding generation.</summary>
    [JsonPropertyName("embeddingEndpoint")]
    public string EmbeddingEndpoint { get; set; } = "";

    /// <summary>API key for the embedding endpoint.</summary>
    [JsonPropertyName("embeddingApiKey")]
    public string EmbeddingApiKey { get; set; } = "";

    /// <summary>Deployment name for the embedding model (e.g. text-embedding-3-small).</summary>
    [JsonPropertyName("embeddingDeployment")]
    public string EmbeddingDeployment { get; set; } = "";

    /// <summary>Hour (UTC) to run the nightly memory index job.</summary>
    [JsonPropertyName("indexRunAtHour")]
    public int IndexRunAtHour { get; set; } = 2;
```

- [ ] **Step 5: Build to verify**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj \
        src/agent/OpenAgent.sln \
        src/agent/OpenAgent/OpenAgent.csproj \
        src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj \
        src/agent/OpenAgent.Models/Configs/AgentConfig.cs
git commit -m "feat(memory): scaffold OpenAgent.MemoryIndex project and AgentConfig fields"
```

---

## Task 2: MemoryChunkStore — SQLite Persistence

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs`
- Create: `src/agent/OpenAgent.Tests/MemoryChunkStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/MemoryChunkStoreTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.MemoryIndex;

namespace OpenAgent.Tests;

public class MemoryChunkStoreTests : IDisposable
{
    private readonly string _dbDir;
    private readonly MemoryChunkStore _store;

    public MemoryChunkStoreTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"memchunk-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbDir);
        var dbPath = Path.Combine(_dbDir, "memory.db");
        _store = new MemoryChunkStore(dbPath, NullLogger<MemoryChunkStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    [Fact]
    public void InsertChunks_and_GetProcessedDates_roundtrips()
    {
        _store.InsertChunks("2026-04-15", [
            new ChunkEntry { Content = "Topic A", Embedding = [1f, 2f, 3f] },
            new ChunkEntry { Content = "Topic B", Embedding = [4f, 5f, 6f] }
        ]);

        var dates = _store.GetProcessedDates();
        Assert.Single(dates);
        Assert.Contains("2026-04-15", dates);
    }

    [Fact]
    public void InsertChunks_duplicate_date_throws()
    {
        var chunks = new[] { new ChunkEntry { Content = "A", Embedding = [1f] } };
        _store.InsertChunks("2026-04-15", chunks);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => _store.InsertChunks("2026-04-15", chunks));
    }

    [Fact]
    public void Embedding_serialization_roundtrips()
    {
        var original = new float[] { 1.5f, -2.3f, 0f, float.MaxValue, float.MinValue };
        var bytes = MemoryChunkStore.SerializeEmbedding(original);
        var restored = MemoryChunkStore.DeserializeEmbedding(bytes);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void GetAllChunks_returns_content_and_embeddings()
    {
        _store.InsertChunks("2026-04-14", [
            new ChunkEntry { Content = "Day one topic", Embedding = [0.1f, 0.2f] }
        ]);
        _store.InsertChunks("2026-04-15", [
            new ChunkEntry { Content = "Day two A", Embedding = [0.3f, 0.4f] },
            new ChunkEntry { Content = "Day two B", Embedding = [0.5f, 0.6f] }
        ]);

        var chunks = _store.GetAllChunks();

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Day one topic", chunks[0].Content);
        Assert.Equal(new float[] { 0.1f, 0.2f }, chunks[0].Embedding);
    }

    [Fact]
    public void GetStats_empty_store()
    {
        var stats = _store.GetStats();
        Assert.Equal(0, stats.TotalChunks);
        Assert.Equal(0, stats.TotalDays);
        Assert.Null(stats.OldestDate);
    }

    [Fact]
    public void GetStats_with_entries()
    {
        _store.InsertChunks("2026-04-14", [
            new ChunkEntry { Content = "A", Embedding = [1f] },
            new ChunkEntry { Content = "B", Embedding = [1f] }
        ]);
        _store.InsertChunks("2026-04-15", [
            new ChunkEntry { Content = "C", Embedding = [1f] }
        ]);

        var stats = _store.GetStats();

        Assert.Equal(3, stats.TotalChunks);
        Assert.Equal(2, stats.TotalDays);
        Assert.Equal("2026-04-14", stats.OldestDate);
        Assert.Equal("2026-04-15", stats.NewestDate);
    }

    [Fact]
    public void CosineSimilarity_identical_vectors()
    {
        var v = new float[] { 1f, 2f, 3f };
        Assert.Equal(1f, MemoryChunkStore.CosineSimilarity(v, v), 5);
    }

    [Fact]
    public void CosineSimilarity_orthogonal_vectors()
    {
        Assert.Equal(0f, MemoryChunkStore.CosineSimilarity([1f, 0f], [0f, 1f]), 5);
    }

    [Fact]
    public void CosineSimilarity_zero_vector()
    {
        Assert.Equal(0f, MemoryChunkStore.CosineSimilarity([1f, 2f], [0f, 0f]), 5);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "MemoryChunkStoreTests" -v q
```

Expected: Compilation error — types don't exist yet.

- [ ] **Step 3: Implement MemoryChunkStore**

Create `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs`:

```csharp
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Input for inserting a chunk — content + embedding, no date/index (assigned by store).
/// </summary>
public sealed record ChunkEntry
{
    public required string Content { get; init; }
    public required float[] Embedding { get; init; }
}

/// <summary>
/// A stored chunk with full context — returned by search and bulk load.
/// </summary>
public sealed record StoredChunk
{
    public required string Date { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Content { get; init; }
    public required float[] Embedding { get; init; }
}

/// <summary>
/// Aggregate stats about the memory chunk store.
/// </summary>
public sealed record ChunkStats
{
    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; init; }

    [JsonPropertyName("totalDays")]
    public int TotalDays { get; init; }

    [JsonPropertyName("oldestDate")]
    public string? OldestDate { get; init; }

    [JsonPropertyName("newestDate")]
    public string? NewestDate { get; init; }
}

/// <summary>
/// SQLite-backed storage for memory chunks. Embeddings stored as BLOBs,
/// similarity computed in-memory.
/// </summary>
public sealed class MemoryChunkStore
{
    private readonly string _connectionString;
    private readonly ILogger<MemoryChunkStore> _logger;

    public MemoryChunkStore(AgentEnvironment environment, ILogger<MemoryChunkStore> logger)
    {
        var dbPath = Path.Combine(environment.DataPath, "memory.db");
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
        InitializeDatabase();
    }

    internal MemoryChunkStore(string dbPath, ILogger<MemoryChunkStore> logger)
    {
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = Open();

        // WAL is persistent once set — only needs to run once
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_chunks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                date        TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                content     TEXT NOT NULL,
                embedding   BLOB NOT NULL,
                UNIQUE(date, chunk_index)
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("Memory chunk store initialized");
    }

    /// <summary>Inserts all chunks for a given date. Assigns chunk_index 0..N.</summary>
    public void InsertChunks(string date, IReadOnlyList<ChunkEntry> chunks)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        for (int i = 0; i < chunks.Count; i++)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memory_chunks (date, chunk_index, content, embedding)
                VALUES (@date, @chunkIndex, @content, @embedding)
                """;
            cmd.Parameters.AddWithValue("@date", date);
            cmd.Parameters.AddWithValue("@chunkIndex", i);
            cmd.Parameters.AddWithValue("@content", chunks[i].Content);
            cmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(chunks[i].Embedding));
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Returns all dates that have chunks (for filtering during scan).</summary>
    public HashSet<string> GetProcessedDates()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT date FROM memory_chunks";
        using var reader = cmd.ExecuteReader();
        var dates = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            dates.Add(reader.GetString(0));
        return dates;
    }

    /// <summary>Loads all chunks with embeddings for in-memory similarity search.</summary>
    public List<StoredChunk> GetAllChunks()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT date, chunk_index, content, embedding FROM memory_chunks ORDER BY date, chunk_index";
        using var reader = cmd.ExecuteReader();
        var chunks = new List<StoredChunk>();
        while (reader.Read())
        {
            chunks.Add(new StoredChunk
            {
                Date = reader.GetString(0),
                ChunkIndex = reader.GetInt32(1),
                Content = reader.GetString(2),
                Embedding = DeserializeEmbedding((byte[])reader["embedding"])
            });
        }
        return chunks;
    }

    /// <summary>Aggregate stats for the stats endpoint.</summary>
    public ChunkStats GetStats()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                COUNT(DISTINCT date),
                MIN(date),
                MAX(date)
            FROM memory_chunks
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var total = reader.GetInt32(0);
        return new ChunkStats
        {
            TotalChunks = total,
            TotalDays = reader.GetInt32(1),
            OldestDate = total > 0 ? reader.GetString(2) : null,
            NewestDate = total > 0 ? reader.GetString(3) : null
        };
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    public static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] DeserializeEmbedding(byte[] bytes)
    {
        var embedding = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
        return embedding;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "MemoryChunkStoreTests" -v q
```

Expected: All 9 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs \
        src/agent/OpenAgent.Tests/MemoryChunkStoreTests.cs
git commit -m "feat(memory): add MemoryChunkStore with SQLite persistence and cosine similarity"
```

---

## Task 3: EmbeddingClient — Azure OpenAI

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/EmbeddingClient.cs`
- Create: `src/agent/OpenAgent.Tests/Fakes/FakeEmbeddingHandler.cs`
- Create: `src/agent/OpenAgent.Tests/EmbeddingClientTests.cs`

- [ ] **Step 1: Create the test HTTP handler**

Create `src/agent/OpenAgent.Tests/Fakes/FakeEmbeddingHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// HttpMessageHandler that returns canned JSON for embedding API tests.
/// </summary>
public sealed class FakeEmbeddingHandler(string responseJson) : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `src/agent/OpenAgent.Tests/EmbeddingClientTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class EmbeddingClientTests
{
    [Fact]
    public void ParseEmbeddingResponse_extracts_floats()
    {
        var json = """
            {
              "data": [{ "embedding": [0.1, 0.2, 0.3, -0.4], "index": 0 }],
              "model": "text-embedding-3-small",
              "usage": { "prompt_tokens": 5, "total_tokens": 5 }
            }
            """;

        var embedding = EmbeddingClient.ParseEmbeddingResponse(json);

        Assert.Equal(4, embedding.Length);
        Assert.Equal(0.1f, embedding[0], 5);
        Assert.Equal(-0.4f, embedding[3], 5);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_sends_correct_request()
    {
        var handler = new FakeEmbeddingHandler(
            """{"data":[{"embedding":[0.5,0.6],"index":0}]}""");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://test.openai.azure.com/")
        };
        var config = new AgentConfig { EmbeddingDeployment = "text-embedding-3-small" };
        using var client = new EmbeddingClient(httpClient, config,
            NullLogger<EmbeddingClient>.Instance);

        var result = await client.GenerateEmbeddingAsync("test text");

        Assert.Equal(2, result.Length);
        Assert.Contains("text-embedding-3-small", handler.LastRequestUri!.ToString());
        Assert.Contains("embeddings", handler.LastRequestUri.ToString());
        Assert.Contains("test text", handler.LastRequestBody!);
    }

    [Fact]
    public void IsConfigured_false_when_empty()
    {
        using var client = new EmbeddingClient(new AgentConfig(),
            NullLogger<EmbeddingClient>.Instance);
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void IsConfigured_true_when_all_set()
    {
        var config = new AgentConfig
        {
            EmbeddingEndpoint = "https://test.openai.azure.com",
            EmbeddingApiKey = "key123",
            EmbeddingDeployment = "text-embedding-3-small"
        };
        using var client = new EmbeddingClient(config,
            NullLogger<EmbeddingClient>.Instance);
        Assert.True(client.IsConfigured);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "EmbeddingClientTests" -v q
```

Expected: Compilation error — `EmbeddingClient` doesn't exist yet.

- [ ] **Step 4: Implement EmbeddingClient**

Create `src/agent/OpenAgent.MemoryIndex/EmbeddingClient.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Generates text embeddings via the Azure OpenAI Embeddings API.
/// </summary>
public sealed class EmbeddingClient : IDisposable
{
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<EmbeddingClient> _logger;
    private HttpClient? _httpClient;

    public EmbeddingClient(AgentConfig agentConfig, ILogger<EmbeddingClient> logger)
    {
        _agentConfig = agentConfig;
        _logger = logger;
    }

    /// <summary>Test constructor — accepts a pre-built HttpClient.</summary>
    internal EmbeddingClient(HttpClient httpClient, AgentConfig agentConfig, ILogger<EmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    /// <summary>True when all embedding config fields are set.</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_agentConfig.EmbeddingEndpoint)
        && !string.IsNullOrEmpty(_agentConfig.EmbeddingApiKey)
        && !string.IsNullOrEmpty(_agentConfig.EmbeddingDeployment);

    /// <summary>Generates an embedding vector for the given text.</summary>
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var client = EnsureHttpClient();
        var url = $"openai/deployments/{_agentConfig.EmbeddingDeployment}/embeddings?api-version=2024-02-01";
        var body = JsonSerializer.Serialize(new { input = text });
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        _logger.LogDebug("Embedding generated for {Length} chars", text.Length);
        return ParseEmbeddingResponse(json);
    }

    internal static float[] ParseEmbeddingResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var embedding = new float[data.GetArrayLength()];
        int i = 0;
        foreach (var el in data.EnumerateArray())
            embedding[i++] = el.GetSingle();
        return embedding;
    }

    private HttpClient EnsureHttpClient()
    {
        if (_httpClient is not null) return _httpClient;
        var baseUri = _agentConfig.EmbeddingEndpoint.TrimEnd('/');
        _httpClient = new HttpClient { BaseAddress = new Uri(baseUri + "/") };
        _httpClient.DefaultRequestHeaders.Add("api-key", _agentConfig.EmbeddingApiKey);
        return _httpClient;
    }

    public void Dispose() => _httpClient?.Dispose();
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "EmbeddingClientTests" -v q
```

Expected: All 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/EmbeddingClient.cs \
        src/agent/OpenAgent.Tests/EmbeddingClientTests.cs \
        src/agent/OpenAgent.Tests/Fakes/FakeEmbeddingHandler.cs
git commit -m "feat(memory): add EmbeddingClient for Azure OpenAI embeddings"
```

---

## Task 4: MemoryChunker — LLM Topic Splitting

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryChunker.cs`
- Create: `src/agent/OpenAgent.Tests/MemoryChunkerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/MemoryChunkerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryChunkerTests
{
    [Fact]
    public void ParseChunksResponse_extracts_strings()
    {
        var json = """{"chunks": ["Topic one content", "Topic two content", "Topic three"]}""";
        var chunks = MemoryChunker.ParseChunksResponse(json);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Topic one content", chunks[0]);
        Assert.Equal("Topic three", chunks[2]);
    }

    [Fact]
    public void ParseChunksResponse_empty_array()
    {
        var json = """{"chunks": []}""";
        var chunks = MemoryChunker.ParseChunksResponse(json);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkFileAsync_calls_llm_and_returns_chunks()
    {
        var fakeProvider = new StreamingTextProvider(
            """{"chunks": ["Auth discussion", "Pasta recipe"]}""");
        ILlmTextProvider Factory(string _) => fakeProvider;
        var config = new AgentConfig { CompactionProvider = "fake", CompactionModel = "fake" };
        var chunker = new MemoryChunker(Factory, config,
            NullLogger<MemoryChunker>.Instance);

        var chunks = await chunker.ChunkFileAsync("Raw daily log content here...");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Auth discussion", chunks[0]);
        Assert.Equal("Pasta recipe", chunks[1]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "MemoryChunkerTests" -v q
```

Expected: Compilation error — `MemoryChunker` doesn't exist yet.

- [ ] **Step 3: Implement MemoryChunker**

Create `src/agent/OpenAgent.MemoryIndex/MemoryChunker.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Uses an LLM to split a raw daily memory log into self-contained topic chunks.
/// </summary>
public sealed class MemoryChunker
{
    private const string SystemPrompt = """
        Restructure this daily memory log into self-contained topic chunks.

        Rules:
        - Each chunk covers one topic or conversation thread
        - Each chunk must be understandable on its own, without the other chunks
        - Preserve all factual information — names, dates, decisions, URLs
        - Don't add information that wasn't in the original
        - Don't merge unrelated topics into one chunk
        - If the entire file is one topic, return a single chunk

        Output JSON: { "chunks": ["chunk text 1", "chunk text 2", ...] }
        """;

    private readonly Func<string, ILlmTextProvider> _providerFactory;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<MemoryChunker> _logger;

    public MemoryChunker(
        Func<string, ILlmTextProvider> providerFactory,
        AgentConfig agentConfig,
        ILogger<MemoryChunker> logger)
    {
        _providerFactory = providerFactory;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    /// <summary>Chunks a raw daily log into topic segments via LLM.</summary>
    public async Task<IReadOnlyList<string>> ChunkFileAsync(string fileContent, CancellationToken ct = default)
    {
        // Raw CompleteAsync overload — no persistence, no tool calls.
        // Same Message ID pattern as CompactionSummarizer.
        var provider = _providerFactory(_agentConfig.CompactionProvider);
        var messages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = SystemPrompt },
            new() { Id = "usr", ConversationId = "", Role = "user", Content = fileContent }
        };
        var options = new CompletionOptions { ResponseFormat = "json_object" };

        var fullContent = new StringBuilder();
        await foreach (var evt in provider.CompleteAsync(messages, _agentConfig.CompactionModel, options, ct))
        {
            if (evt is TextDelta delta)
                fullContent.Append(delta.Content);
        }

        var chunks = ParseChunksResponse(fullContent.ToString());
        _logger.LogInformation("Chunked file into {Count} topics", chunks.Count);
        return chunks;
    }

    /// <summary>Parses the LLM JSON response into a list of chunk strings.</summary>
    internal static IReadOnlyList<string> ParseChunksResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var array = doc.RootElement.GetProperty("chunks");
        var chunks = new List<string>(array.GetArrayLength());
        foreach (var el in array.EnumerateArray())
        {
            var text = el.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                chunks.Add(text);
        }
        return chunks;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "MemoryChunkerTests" -v q
```

Expected: All 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryChunker.cs \
        src/agent/OpenAgent.Tests/MemoryChunkerTests.cs
git commit -m "feat(memory): add MemoryChunker for LLM-based topic splitting"
```

---

## Task 5: MemoryIndexService — Indexing + Search Orchestration

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs`
- Create: `src/agent/OpenAgent.Tests/MemoryIndexServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

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
    private readonly string _dataDir;
    private readonly string _memoryDir;
    private readonly MemoryChunkStore _store;
    private readonly AgentConfig _agentConfig;

    public MemoryIndexServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"memservice-test-{Guid.NewGuid()}");
        _memoryDir = Path.Combine(_dataDir, "memory");
        Directory.CreateDirectory(_memoryDir);

        var dbPath = Path.Combine(_dataDir, "memory.db");
        _store = new MemoryChunkStore(dbPath, NullLogger<MemoryChunkStore>.Instance);
        _agentConfig = new AgentConfig
        {
            CompactionProvider = "fake",
            CompactionModel = "fake",
            EmbeddingDeployment = "test",
            MemoryDays = 3
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private MemoryIndexService CreateService(EmbeddingClient? embeddingClient = null)
    {
        embeddingClient ??= CreateFakeEmbeddingClient();
        var fakeProvider = new StreamingTextProvider(
            """{"chunks": ["Test chunk content"]}""");
        ILlmTextProvider Factory(string _) => fakeProvider;
        var chunker = new MemoryChunker(Factory, _agentConfig,
            NullLogger<MemoryChunker>.Instance);
        var env = new AgentEnvironment { DataPath = _dataDir };

        return new MemoryIndexService(
            _store, chunker, embeddingClient, _agentConfig, env,
            NullLogger<MemoryIndexService>.Instance);
    }

    private EmbeddingClient CreateFakeEmbeddingClient()
    {
        var handler = new FakeEmbeddingHandler(
            """{"data":[{"embedding":[0.1,0.2,0.3],"index":0}]}""");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test/") };
        return new EmbeddingClient(httpClient, _agentConfig,
            NullLogger<EmbeddingClient>.Instance);
    }

    private void WriteMemoryFile(string date, string content)
    {
        File.WriteAllText(Path.Combine(_memoryDir, $"{date}.md"), content);
    }

    [Fact]
    public async Task RunAsync_processes_files_past_memory_window()
    {
        // memoryDays=3, so files 3+ days old should be processed
        var oldDate = DateTime.UtcNow.AddDays(-4).ToString("yyyy-MM-dd");
        WriteMemoryFile(oldDate, new string('x', 100));

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(1, result.FilesProcessed);
        Assert.True(result.ChunksCreated > 0);
        Assert.False(File.Exists(Path.Combine(_memoryDir, $"{oldDate}.md")),
            "Source file should be deleted after processing");
    }

    [Fact]
    public async Task RunAsync_skips_files_within_memory_window()
    {
        // Yesterday is within the 3-day window — should NOT be processed
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        WriteMemoryFile(yesterday, new string('x', 100));

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(0, result.FilesProcessed);
        Assert.True(File.Exists(Path.Combine(_memoryDir, $"{yesterday}.md")),
            "File within window should stay on disk");
    }

    [Fact]
    public async Task RunAsync_skips_already_processed_dates()
    {
        var oldDate = DateTime.UtcNow.AddDays(-5).ToString("yyyy-MM-dd");
        WriteMemoryFile(oldDate, new string('x', 100));
        _store.InsertChunks(oldDate, [new ChunkEntry { Content = "Already done", Embedding = [0.1f] }]);

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(0, result.FilesProcessed);
    }

    [Fact]
    public async Task RunAsync_skips_short_files()
    {
        var oldDate = DateTime.UtcNow.AddDays(-5).ToString("yyyy-MM-dd");
        WriteMemoryFile(oldDate, "Short");

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(0, result.FilesProcessed);
    }

    [Fact]
    public async Task SearchAsync_returns_ranked_results()
    {
        _store.InsertChunks("2026-04-14", [
            new ChunkEntry { Content = "Cooking recipes", Embedding = [1f, 0f, 0f] },
        ]);
        _store.InsertChunks("2026-04-15", [
            new ChunkEntry { Content = "Programming tasks", Embedding = [0f, 1f, 0f] },
        ]);

        // Fake embedding returns [1,0,0] — should rank "cooking" highest
        var handler = new FakeEmbeddingHandler(
            """{"data":[{"embedding":[1.0,0.0,0.0],"index":0}]}""");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test/") };
        var embClient = new EmbeddingClient(httpClient, _agentConfig,
            NullLogger<EmbeddingClient>.Instance);

        var service = CreateService(embClient);
        var results = await service.SearchAsync("cooking", 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("Cooking recipes", results[0].Content);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task RunAsync_empty_memory_dir()
    {
        var service = CreateService();
        var result = await service.RunAsync();
        Assert.Equal(0, result.FilesScanned);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "MemoryIndexServiceTests" -v q
```

Expected: Compilation error — `MemoryIndexService` doesn't exist yet.

- [ ] **Step 3: Implement MemoryIndexService**

Create `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs`:

```csharp
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Result of an indexing run.
/// </summary>
public sealed record IndexResult
{
    [JsonPropertyName("filesScanned")]
    public int FilesScanned { get; init; }

    [JsonPropertyName("filesProcessed")]
    public int FilesProcessed { get; init; }

    [JsonPropertyName("chunksCreated")]
    public int ChunksCreated { get; init; }

    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// A single search result — chunk content with relevance score.
/// </summary>
public sealed record SearchResult
{
    [JsonPropertyName("date")]
    public required string Date { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("score")]
    public required float Score { get; init; }
}

/// <summary>
/// Orchestrates memory indexing (scan, chunk, embed, store, delete) and search.
/// </summary>
public sealed class MemoryIndexService
{
    private readonly MemoryChunkStore _store;
    private readonly MemoryChunker _chunker;
    private readonly EmbeddingClient _embeddingClient;
    private readonly AgentConfig _agentConfig;
    private readonly AgentEnvironment _environment;
    private readonly ILogger<MemoryIndexService> _logger;
    private List<StoredChunk>? _cachedChunks;

    public MemoryIndexService(
        MemoryChunkStore store,
        MemoryChunker chunker,
        EmbeddingClient embeddingClient,
        AgentConfig agentConfig,
        AgentEnvironment environment,
        ILogger<MemoryIndexService> logger)
    {
        _store = store;
        _chunker = chunker;
        _embeddingClient = embeddingClient;
        _agentConfig = agentConfig;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Scans for daily logs past the memory window, chunks, embeds, stores, deletes source.</summary>
    public async Task<IndexResult> RunAsync(CancellationToken ct = default)
    {
        var memoryDir = Path.Combine(_environment.DataPath, "memory");
        if (!Directory.Exists(memoryDir))
            return new IndexResult();

        // Files older than memoryDays are eligible for processing
        var cutoffDate = DateTime.UtcNow.AddDays(-Math.Max(1, _agentConfig.MemoryDays))
            .ToString("yyyy-MM-dd");

        var allFiles = Directory.GetFiles(memoryDir, "????-??-??.md")
            .Where(f => string.Compare(Path.GetFileNameWithoutExtension(f), cutoffDate, StringComparison.Ordinal) < 0)
            .OrderBy(f => f)
            .ToList();

        var processedDates = _store.GetProcessedDates();
        var unprocessed = allFiles
            .Where(f => !processedDates.Contains(Path.GetFileNameWithoutExtension(f)))
            .ToList();

        int processed = 0, chunksCreated = 0;
        var errors = new List<string>();

        foreach (var filePath in unprocessed)
        {
            try
            {
                var content = await File.ReadAllTextAsync(filePath, ct);
                if (content.Length < 50)
                    continue;

                // LLM chunks the file into topics
                var chunkTexts = await _chunker.ChunkFileAsync(content, ct);
                if (chunkTexts.Count == 0)
                    continue;

                // Embed each chunk
                var entries = new List<ChunkEntry>(chunkTexts.Count);
                foreach (var text in chunkTexts)
                {
                    var embedding = await _embeddingClient.GenerateEmbeddingAsync(text, ct);
                    entries.Add(new ChunkEntry { Content = text, Embedding = embedding });
                }

                // Store and delete source
                var date = Path.GetFileNameWithoutExtension(filePath);
                _store.InsertChunks(date, entries);
                File.Delete(filePath);

                processed++;
                chunksCreated += entries.Count;
                _logger.LogInformation("Indexed {Date}: {ChunkCount} chunks", date, entries.Count);
            }
            catch (Exception ex)
            {
                var fileName = Path.GetFileName(filePath);
                errors.Add($"{fileName}: {ex.Message}");
                _logger.LogError(ex, "Failed to index {FilePath}", filePath);
            }
        }

        // Invalidate search cache
        _cachedChunks = null;

        return new IndexResult
        {
            FilesScanned = allFiles.Count,
            FilesProcessed = processed,
            ChunksCreated = chunksCreated,
            Errors = errors
        };
    }

    /// <summary>Embeds the query and finds the most similar chunks.</summary>
    public async Task<List<SearchResult>> SearchAsync(string query, int limit = 5, CancellationToken ct = default)
    {
        var queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query, ct);
        var chunks = _cachedChunks ??= _store.GetAllChunks();

        return chunks
            .Select(c => new SearchResult
            {
                Date = c.Date,
                Content = c.Content,
                Score = MemoryChunkStore.CosineSimilarity(queryEmbedding, c.Embedding)
            })
            .OrderByDescending(r => r.Score)
            .Take(Math.Min(limit, 20))
            .ToList();
    }

    /// <summary>Returns aggregate chunk stats.</summary>
    public ChunkStats GetStats() => _store.GetStats();
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "MemoryIndexServiceTests" -v q
```

Expected: All 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs \
        src/agent/OpenAgent.Tests/MemoryIndexServiceTests.cs
git commit -m "feat(memory): add MemoryIndexService for chunk-based indexing and search"
```

---

## Task 6: MemoryToolHandler — search_memory Tool

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs`
- Create: `src/agent/OpenAgent.Tests/MemoryToolHandlerTests.cs`

- [ ] **Step 1: Write the failing tests**

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
    private readonly string _dataDir;
    private readonly MemoryChunkStore _store;
    private readonly MemoryToolHandler _handler;
    private readonly EmbeddingClient _embeddingClient;

    public MemoryToolHandlerTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"memtool-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(_dataDir, "memory"));

        var dbPath = Path.Combine(_dataDir, "memory.db");
        _store = new MemoryChunkStore(dbPath, NullLogger<MemoryChunkStore>.Instance);

        var agentConfig = new AgentConfig
        {
            CompactionProvider = "fake",
            CompactionModel = "fake",
            EmbeddingDeployment = "test"
        };

        var embHandler = new FakeEmbeddingHandler(
            """{"data":[{"embedding":[1.0,0.0,0.0],"index":0}]}""");
        var httpClient = new HttpClient(embHandler) { BaseAddress = new Uri("https://test/") };
        _embeddingClient = new EmbeddingClient(httpClient, agentConfig,
            NullLogger<EmbeddingClient>.Instance);

        var fakeProvider = new StreamingTextProvider("""{"chunks":["Test"]}""");
        ILlmTextProvider Factory(string _) => fakeProvider;
        var chunker = new MemoryChunker(Factory, agentConfig,
            NullLogger<MemoryChunker>.Instance);
        var env = new AgentEnvironment { DataPath = _dataDir };
        var service = new MemoryIndexService(
            _store, chunker, _embeddingClient, agentConfig, env,
            NullLogger<MemoryIndexService>.Instance);
        _handler = new MemoryToolHandler(service, _embeddingClient);
    }

    public void Dispose()
    {
        _embeddingClient.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    [Fact]
    public void Exposes_one_tool_when_configured()
    {
        Assert.Single(_handler.Tools);
        Assert.Equal("search_memory", _handler.Tools[0].Definition.Name);
    }

    [Fact]
    public void Exposes_no_tools_when_unconfigured()
    {
        var unconfigured = new EmbeddingClient(new AgentConfig(),
            NullLogger<EmbeddingClient>.Instance);
        var handler = new MemoryToolHandler(null!, unconfigured);
        Assert.Empty(handler.Tools);
    }

    [Fact]
    public async Task SearchMemory_returns_chunk_content()
    {
        _store.InsertChunks("2026-04-14", [
            new ChunkEntry { Content = "Cooking recipes discussion", Embedding = [1f, 0f, 0f] },
        ]);
        _store.InsertChunks("2026-04-15", [
            new ChunkEntry { Content = "Programming tasks", Embedding = [0f, 1f, 0f] },
        ]);

        var tool = _handler.Tools[0];
        var result = await tool.ExecuteAsync("""{"query":"cooking"}""", "test-conv");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");

        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("Cooking recipes discussion", results[0].GetProperty("content").GetString());
        Assert.Equal("2026-04-14", results[0].GetProperty("date").GetString());
    }

    [Fact]
    public async Task SearchMemory_respects_limit()
    {
        _store.InsertChunks("2026-04-14", [
            new ChunkEntry { Content = "A", Embedding = [1f, 0f, 0f] },
            new ChunkEntry { Content = "B", Embedding = [0f, 1f, 0f] },
        ]);

        var tool = _handler.Tools[0];
        var result = await tool.ExecuteAsync("""{"query":"test","limit":1}""", "test-conv");
        var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("results").GetArrayLength());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "MemoryToolHandlerTests" -v q
```

Expected: Compilation error.

- [ ] **Step 3: Implement MemoryToolHandler**

Create `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Provides the search_memory tool to the agent.
/// Tool only exposed when embedding config is present.
/// </summary>
public sealed class MemoryToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public MemoryToolHandler(MemoryIndexService service, EmbeddingClient embeddingClient)
    {
        Tools = embeddingClient.IsConfigured
            ? [new SearchMemoryTool(service)]
            : [];
    }
}

/// <summary>
/// Searches past memories by topic, returning matching chunk content directly.
/// </summary>
internal sealed class SearchMemoryTool(MemoryIndexService service) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "search_memory",
        Description = "Search past memories by topic. Returns the most relevant chunks from daily logs with their content and date.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Natural language search query" },
                limit = new { type = "integer", description = "Maximum results to return (default 5, max 20)" }
            },
            required = new[] { "query" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            var query = args.GetProperty("query").GetString()
                ?? throw new ArgumentException("query is required");
            var limit = args.TryGetProperty("limit", out var limitEl) && limitEl.ValueKind == JsonValueKind.Number
                ? limitEl.GetInt32() : 5;

            var results = await service.SearchAsync(query, limit, ct);
            return JsonSerializer.Serialize(new { results });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "MemoryToolHandlerTests" -v q
```

Expected: All 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs \
        src/agent/OpenAgent.Tests/MemoryToolHandlerTests.cs
git commit -m "feat(memory): add search_memory agent tool"
```

---

## Task 7: Hosted Service + Endpoints + DI Wiring

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs`
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs`
- Create: `src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs`
- Create: `src/agent/OpenAgent.Tests/MemoryIndexHostedServiceTests.cs`
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Implement the hosted service**

Create `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Runs the memory index job on a daily schedule.
/// Checks hourly; runs once per day at the configured hour (UTC).
/// </summary>
public sealed class MemoryIndexHostedService : IHostedService, IDisposable
{
    private readonly MemoryIndexService _service;
    private readonly EmbeddingClient _embeddingClient;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<MemoryIndexHostedService> _logger;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _timerTask;
    private DateOnly? _lastRunDate;
    private bool _configWarningLogged;

    public MemoryIndexHostedService(
        MemoryIndexService service,
        EmbeddingClient embeddingClient,
        AgentConfig agentConfig,
        ILogger<MemoryIndexHostedService> logger)
    {
        _service = service;
        _embeddingClient = embeddingClient;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(TimeSpan.FromHours(1));
        _timerTask = RunTimerLoop(_cts.Token);
        _logger.LogInformation("Memory index hosted service started");
        return Task.CompletedTask;
    }

    private async Task RunTimerLoop(CancellationToken ct)
    {
        while (await _timer!.WaitForNextTickAsync(ct))
        {
            try { await CheckAndRunAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Memory index timer tick failed"); }
        }
    }

    internal async Task CheckAndRunAsync(CancellationToken ct)
    {
        if (!_embeddingClient.IsConfigured)
        {
            if (!_configWarningLogged)
            {
                _logger.LogWarning("Memory index not configured — set embeddingEndpoint in agent config");
                _configWarningLogged = true;
            }
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_lastRunDate == today) return;
        if (DateTime.UtcNow.Hour < _agentConfig.IndexRunAtHour) return;

        _logger.LogInformation("Starting nightly memory index run");
        var result = await _service.RunAsync(ct);
        _lastRunDate = today;

        _logger.LogInformation(
            "Memory index complete: scanned={Scanned}, processed={Processed}, chunks={Chunks}, errors={Errors}",
            result.FilesScanned, result.FilesProcessed, result.ChunksCreated, result.Errors.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_timerTask is not null)
        {
            try { await _timerTask; }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Dispose();
    }
}
```

- [ ] **Step 2: Implement the endpoints**

Create `src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// REST endpoints for manual index triggering and stats.
/// </summary>
public static class MemoryIndexEndpoints
{
    public static void MapMemoryIndexEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/memory-index").RequireAuthorization();

        group.MapPost("/run", async (MemoryIndexService service, CancellationToken ct) =>
        {
            var result = await service.RunAsync(ct);
            return Results.Ok(result);
        });

        group.MapGet("/stats", (MemoryIndexService service) =>
        {
            return Results.Ok(service.GetStats());
        });
    }
}
```

- [ ] **Step 3: Implement DI extensions**

Create `src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// DI registration for the memory index subsystem.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryIndex(this IServiceCollection services)
    {
        services.AddSingleton<MemoryChunkStore>();
        services.AddSingleton<EmbeddingClient>();
        services.AddSingleton<MemoryChunker>();
        services.AddSingleton<MemoryIndexService>();
        services.AddSingleton<IToolHandler, MemoryToolHandler>();
        services.AddHostedService<MemoryIndexHostedService>();
        return services;
    }
}
```

- [ ] **Step 4: Write hosted service guard tests**

Create `src/agent/OpenAgent.Tests/MemoryIndexHostedServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryIndexHostedServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly MemoryIndexService _service;
    private readonly AgentConfig _agentConfig;

    public MemoryIndexHostedServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"memhosted-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(_dataDir, "memory"));

        var dbPath = Path.Combine(_dataDir, "memory.db");
        var store = new MemoryChunkStore(dbPath, NullLogger<MemoryChunkStore>.Instance);

        _agentConfig = new AgentConfig
        {
            CompactionProvider = "fake",
            CompactionModel = "fake",
            EmbeddingDeployment = "test",
            IndexRunAtHour = 0
        };

        var embHandler = new FakeEmbeddingHandler(
            """{"data":[{"embedding":[0.1],"index":0}]}""");
        var httpClient = new HttpClient(embHandler) { BaseAddress = new Uri("https://test/") };
        var embClient = new EmbeddingClient(httpClient, _agentConfig,
            NullLogger<EmbeddingClient>.Instance);

        var fakeProvider = new StreamingTextProvider("""{"chunks":["Test"]}""");
        var chunker = new MemoryChunker(_ => fakeProvider, _agentConfig,
            NullLogger<MemoryChunker>.Instance);
        var env = new AgentEnvironment { DataPath = _dataDir };
        _service = new MemoryIndexService(
            store, chunker, embClient, _agentConfig, env,
            NullLogger<MemoryIndexService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    [Fact]
    public async Task CheckAndRunAsync_skips_when_not_configured()
    {
        var unconfigured = new AgentConfig();
        var unconfiguredClient = new EmbeddingClient(unconfigured,
            NullLogger<EmbeddingClient>.Instance);
        var hosted = new MemoryIndexHostedService(
            _service, unconfiguredClient, unconfigured,
            NullLogger<MemoryIndexHostedService>.Instance);

        await hosted.CheckAndRunAsync(CancellationToken.None);
        // No exception, just returns
    }

    [Fact]
    public async Task CheckAndRunAsync_runs_once_per_day()
    {
        var embHandler = new FakeEmbeddingHandler(
            """{"data":[{"embedding":[0.1],"index":0}]}""");
        var httpClient = new HttpClient(embHandler) { BaseAddress = new Uri("https://test/") };
        var embClient = new EmbeddingClient(httpClient, _agentConfig,
            NullLogger<EmbeddingClient>.Instance);

        var hosted = new MemoryIndexHostedService(
            _service, embClient, _agentConfig,
            NullLogger<MemoryIndexHostedService>.Instance);

        await hosted.CheckAndRunAsync(CancellationToken.None);
        await hosted.CheckAndRunAsync(CancellationToken.None);
        // Second call is a no-op — no exception
    }
}
```

- [ ] **Step 5: Run hosted service tests**

```bash
cd src/agent && dotnet test --filter "MemoryIndexHostedServiceTests" -v q
```

Expected: Both tests pass.

- [ ] **Step 6: Wire into Program.cs**

In `src/agent/OpenAgent/Program.cs`:

Add the using at the top:

```csharp
using OpenAgent.MemoryIndex;
```

Add after `builder.Services.AddScheduledTasks(environment.DataPath);` (around line 84):

```csharp
builder.Services.AddMemoryIndex();
```

Add after `app.MapToolEndpoints();` (around line 190):

```csharp
app.MapMemoryIndexEndpoints();
```

- [ ] **Step 7: Build the full solution**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 8: Run all tests**

```bash
cd src/agent && dotnet test -v q
```

Expected: All tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs \
        src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs \
        src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs \
        src/agent/OpenAgent.Tests/MemoryIndexHostedServiceTests.cs \
        src/agent/OpenAgent/Program.cs
git commit -m "feat(memory): add hosted service, endpoints, DI wiring, and guard tests"
```

---

## Spec Coverage Check

| Spec Section | Task |
|---|---|
| New project: OpenAgent.MemoryIndex | Task 1 |
| AgentConfig additions (4 fields) | Task 1 |
| memory_chunks schema + SQLite persistence | Task 2 |
| Embedding serialization + cosine similarity | Task 2 |
| Azure OpenAI Embeddings API client | Task 3 |
| LLM chunking (file → topic chunks) | Task 4 |
| Indexing flow (scan, chunk, embed, store, delete) | Task 5 |
| Memory window threshold (memoryDays) | Task 5 |
| File deletion after processing | Task 5 |
| Search flow (embed query, similarity, rank) | Task 5 |
| Search chunk caching (invalidated on RunAsync) | Task 5 |
| search_memory tool (returns chunk content) | Task 6 |
| Graceful degradation (no tools when unconfigured) | Task 6 |
| IHostedService with daily timer + guards | Task 7 |
| REST endpoints (run, stats) | Task 7 |
| DI registration | Task 7 |
| Program.cs wiring | Task 7 |
| Modified files (csproj, sln, Program.cs) | Tasks 1, 7 |
| No load_memory_file (YAGNI — file_read exists) | n/a (not built) |
| No preloading from DB | n/a (not built) |
