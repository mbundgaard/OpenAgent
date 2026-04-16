# Memory Index Job Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Index closed daily memory logs into a searchable SQLite vector store with `search_memory` and `load_memory_file` agent tools.

**Architecture:** New `OpenAgent.MemoryIndex` project with 7 files. SQLite stores embeddings as BLOBs; cosine similarity computed in-memory (pure C#, no native deps). Nightly IHostedService timer. Summary generation reuses compaction provider. Azure OpenAI Embeddings API for vectors.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, Azure OpenAI Embeddings API, xUnit

**Spec:** [docs/superpowers/specs/2026-04-16-memory-index-job-design.md](../specs/2026-04-16-memory-index-job-design.md)

---

## File Map

### New files

| File | Responsibility |
|------|---------------|
| `src/agent/OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj` | Project file |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexStore.cs` | SQLite persistence, embedding serialization, cosine similarity |
| `src/agent/OpenAgent.MemoryIndex/EmbeddingClient.cs` | Azure OpenAI Embeddings API client |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs` | Orchestrates indexing and search |
| `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs` | IToolHandler: search_memory, load_memory_file |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs` | IHostedService with daily timer |
| `src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs` | REST API for manual trigger and stats |
| `src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs` | DI registration |
| `src/agent/OpenAgent.Tests/MemoryIndexStoreTests.cs` | Store unit tests |
| `src/agent/OpenAgent.Tests/EmbeddingClientTests.cs` | Embedding client unit tests |
| `src/agent/OpenAgent.Tests/MemoryIndexServiceTests.cs` | Service orchestration tests |
| `src/agent/OpenAgent.Tests/MemoryToolHandlerTests.cs` | Tool execution tests |
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

- [ ] **Step 3: Add the project to the solution**

```bash
cd src/agent && dotnet sln add OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj
```

- [ ] **Step 4: Add project reference from the host project**

Add to `src/agent/OpenAgent/OpenAgent.csproj`, in the `<ItemGroup>` with other ProjectReferences:

```xml
    <ProjectReference Include="..\OpenAgent.MemoryIndex\OpenAgent.MemoryIndex.csproj" />
```

- [ ] **Step 5: Add project reference from the test project**

Add to `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj`, in the `<ItemGroup>` with other ProjectReferences:

```xml
    <ProjectReference Include="..\OpenAgent.MemoryIndex\OpenAgent.MemoryIndex.csproj" />
```

- [ ] **Step 6: Add AgentConfig fields**

Add these 4 properties to the end of `src/agent/OpenAgent.Models/Configs/AgentConfig.cs`, before the closing `}` of the class:

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

- [ ] **Step 7: Build to verify scaffolding**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds with no errors.

- [ ] **Step 8: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj \
        src/agent/OpenAgent.sln \
        src/agent/OpenAgent/OpenAgent.csproj \
        src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj \
        src/agent/OpenAgent.Models/Configs/AgentConfig.cs
git commit -m "feat(memory): scaffold OpenAgent.MemoryIndex project and AgentConfig fields"
```

---

## Task 2: MemoryIndexStore — SQLite Persistence

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryIndexStore.cs`
- Create: `src/agent/OpenAgent.Tests/MemoryIndexStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/MemoryIndexStoreTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.MemoryIndex;

namespace OpenAgent.Tests;

public class MemoryIndexStoreTests : IDisposable
{
    private readonly string _dbDir;
    private readonly MemoryIndexStore _store;

    public MemoryIndexStoreTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"memindex-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbDir);
        var dbPath = Path.Combine(_dbDir, "memory-index.db");
        _store = new MemoryIndexStore(dbPath, NullLogger<MemoryIndexStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    [Fact]
    public void Insert_and_GetIndexedPaths_roundtrips()
    {
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-15.md",
            Summary = "Discussed project plans",
            Embedding = [1.0f, 2.0f, 3.0f],
            IndexedAt = DateTime.UtcNow
        });

        var paths = _store.GetIndexedPaths();
        Assert.Single(paths);
        Assert.Contains("memory/2026-04-15.md", paths);
    }

    [Fact]
    public void Insert_duplicate_filePath_throws()
    {
        var entry = new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-15.md",
            Summary = "Test",
            Embedding = [1.0f],
            IndexedAt = DateTime.UtcNow
        };

        _store.Insert(entry);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _store.Insert(entry));
    }

    [Fact]
    public void Embedding_serialization_roundtrips()
    {
        var original = new float[] { 1.5f, -2.3f, 0.0f, float.MaxValue, float.MinValue };
        var bytes = MemoryIndexStore.SerializeEmbedding(original);
        var restored = MemoryIndexStore.DeserializeEmbedding(bytes);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void GetAllEntries_returns_stored_entries_with_embeddings()
    {
        var embedding1 = new float[] { 0.1f, 0.2f, 0.3f };
        var embedding2 = new float[] { 0.4f, 0.5f, 0.6f };

        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-14.md",
            Summary = "Day one",
            Embedding = embedding1,
            IndexedAt = DateTime.UtcNow
        });
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-15.md",
            Summary = "Day two",
            Embedding = embedding2,
            IndexedAt = DateTime.UtcNow
        });

        var entries = _store.GetAllEntries();

        Assert.Equal(2, entries.Count);
        Assert.Equal(embedding1, entries[0].Embedding);
        Assert.Equal(embedding2, entries[1].Embedding);
    }

    [Fact]
    public void GetStats_empty_store()
    {
        var stats = _store.GetStats();
        Assert.Equal(0, stats.TotalIndexed);
        Assert.Null(stats.LastIndexedAt);
        Assert.Null(stats.OldestEntry);
        Assert.Null(stats.NewestEntry);
    }

    [Fact]
    public void GetStats_with_entries()
    {
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-14.md",
            Summary = "First",
            Embedding = [1.0f],
            IndexedAt = new DateTime(2026, 4, 15, 2, 0, 0, DateTimeKind.Utc)
        });
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-15.md",
            Summary = "Second",
            Embedding = [1.0f],
            IndexedAt = new DateTime(2026, 4, 16, 2, 0, 0, DateTimeKind.Utc)
        });

        var stats = _store.GetStats();

        Assert.Equal(2, stats.TotalIndexed);
        Assert.NotNull(stats.LastIndexedAt);
        Assert.Equal("memory/2026-04-14.md", stats.OldestEntry);
        Assert.Equal("memory/2026-04-15.md", stats.NewestEntry);
    }

    [Fact]
    public void CosineSimilarity_identical_vectors()
    {
        var v = new float[] { 1.0f, 2.0f, 3.0f };
        Assert.Equal(1.0f, MemoryIndexStore.CosineSimilarity(v, v), 5);
    }

    [Fact]
    public void CosineSimilarity_orthogonal_vectors()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f };
        Assert.Equal(0.0f, MemoryIndexStore.CosineSimilarity(a, b), 5);
    }

    [Fact]
    public void CosineSimilarity_opposite_vectors()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { -1.0f, 0.0f };
        Assert.Equal(-1.0f, MemoryIndexStore.CosineSimilarity(a, b), 5);
    }

    [Fact]
    public void CosineSimilarity_zero_vector_returns_zero()
    {
        var a = new float[] { 1.0f, 2.0f };
        var b = new float[] { 0.0f, 0.0f };
        Assert.Equal(0.0f, MemoryIndexStore.CosineSimilarity(a, b), 5);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "MemoryIndexStoreTests" -v q
```

Expected: Compilation error — `MemoryIndexStore` does not exist yet.

- [ ] **Step 3: Implement MemoryIndexStore**

Create `src/agent/OpenAgent.MemoryIndex/MemoryIndexStore.cs`:

```csharp
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Persisted entry in the memory index.
/// </summary>
public sealed record MemoryIndexEntry
{
    public required string FilePath { get; init; }
    public required string Summary { get; init; }
    public required float[] Embedding { get; init; }
    public required DateTime IndexedAt { get; init; }
}

/// <summary>
/// Aggregate stats about the memory index.
/// </summary>
public sealed record IndexStats
{
    [JsonPropertyName("totalIndexed")]
    public int TotalIndexed { get; init; }

    [JsonPropertyName("lastIndexedAt")]
    public DateTime? LastIndexedAt { get; init; }

    [JsonPropertyName("oldestEntry")]
    public string? OldestEntry { get; init; }

    [JsonPropertyName("newestEntry")]
    public string? NewestEntry { get; init; }
}

/// <summary>
/// SQLite-backed storage for the memory vector index.
/// Embeddings stored as BLOBs, similarity computed in-memory.
/// </summary>
public sealed class MemoryIndexStore : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<MemoryIndexStore> _logger;

    public MemoryIndexStore(AgentEnvironment environment, ILogger<MemoryIndexStore> logger)
    {
        var dbPath = Path.Combine(environment.DataPath, "memory-index.db");
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
        InitializeDatabase();
    }

    internal MemoryIndexStore(string dbPath, ILogger<MemoryIndexStore> logger)
    {
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_index (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path   TEXT NOT NULL UNIQUE,
                summary     TEXT NOT NULL,
                embedding   BLOB NOT NULL,
                indexed_at  TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("Memory index store initialized");
    }

    /// <summary>Inserts a new entry. Throws on duplicate file_path.</summary>
    public void Insert(MemoryIndexEntry entry)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_index (file_path, summary, embedding, indexed_at)
            VALUES (@filePath, @summary, @embedding, @indexedAt)
            """;
        cmd.Parameters.AddWithValue("@filePath", entry.FilePath);
        cmd.Parameters.AddWithValue("@summary", entry.Summary);
        cmd.Parameters.AddWithValue("@embedding", SerializeEmbedding(entry.Embedding));
        cmd.Parameters.AddWithValue("@indexedAt", entry.IndexedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns all indexed file paths (for filtering during scan).</summary>
    public HashSet<string> GetIndexedPaths()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT file_path FROM memory_index";
        using var reader = cmd.ExecuteReader();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    /// <summary>Loads all entries with embeddings for in-memory similarity search.</summary>
    public List<MemoryIndexEntry> GetAllEntries()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT file_path, summary, embedding, indexed_at FROM memory_index ORDER BY file_path";
        using var reader = cmd.ExecuteReader();
        var entries = new List<MemoryIndexEntry>();
        while (reader.Read())
        {
            entries.Add(new MemoryIndexEntry
            {
                FilePath = reader.GetString(0),
                Summary = reader.GetString(1),
                Embedding = DeserializeEmbedding((byte[])reader["embedding"]),
                IndexedAt = DateTime.Parse(reader.GetString(3))
            });
        }
        return entries;
    }

    /// <summary>Aggregate stats for the /api/memory-index/stats endpoint.</summary>
    public IndexStats GetStats()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*),
                MAX(indexed_at),
                MIN(file_path),
                MAX(file_path)
            FROM memory_index
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var total = reader.GetInt32(0);
        return new IndexStats
        {
            TotalIndexed = total,
            LastIndexedAt = total > 0 ? DateTime.Parse(reader.GetString(1)) : null,
            OldestEntry = total > 0 ? reader.GetString(2) : null,
            NewestEntry = total > 0 ? reader.GetString(3) : null
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
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Dispose() { }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "MemoryIndexStoreTests" -v q
```

Expected: All 10 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryIndexStore.cs \
        src/agent/OpenAgent.Tests/MemoryIndexStoreTests.cs
git commit -m "feat(memory): add MemoryIndexStore with SQLite persistence and cosine similarity"
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
/// HttpMessageHandler that returns canned JSON responses for embedding API tests.
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
        Assert.Equal(0.2f, embedding[1], 5);
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

        var result = await client.GenerateEmbeddingAsync("test input text");

        Assert.Equal(2, result.Length);
        Assert.Equal(0.5f, result[0], 5);
        Assert.Contains("text-embedding-3-small", handler.LastRequestUri!.ToString());
        Assert.Contains("embeddings", handler.LastRequestUri.ToString());
        Assert.Contains("test input text", handler.LastRequestBody!);
    }

    [Fact]
    public void IsConfigured_returns_false_when_empty()
    {
        using var client = new EmbeddingClient(new AgentConfig(),
            NullLogger<EmbeddingClient>.Instance);
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void IsConfigured_returns_true_when_all_set()
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

Expected: Compilation error — `EmbeddingClient` does not exist yet.

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

    /// <summary>Parses the Azure OpenAI embeddings API response JSON.</summary>
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

## Task 4: MemoryIndexService — Indexing Orchestration

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
    private readonly MemoryIndexStore _store;
    private readonly AgentConfig _agentConfig;

    public MemoryIndexServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"memservice-test-{Guid.NewGuid()}");
        _memoryDir = Path.Combine(_dataDir, "memory");
        Directory.CreateDirectory(_memoryDir);

        var dbPath = Path.Combine(_dataDir, "memory-index.db");
        _store = new MemoryIndexStore(dbPath, NullLogger<MemoryIndexStore>.Instance);
        _agentConfig = new AgentConfig
        {
            CompactionProvider = "fake",
            CompactionModel = "fake-model",
            EmbeddingDeployment = "test"
        };
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private MemoryIndexService CreateService(EmbeddingClient? embeddingClient = null)
    {
        embeddingClient ??= CreateFakeEmbeddingClient();
        var fakeProvider = new StreamingTextProvider("""{"summary": "Test summary of the day"}""");
        ILlmTextProvider Factory(string _) => fakeProvider;
        var env = new AgentEnvironment { DataPath = _dataDir };

        return new MemoryIndexService(
            _store, embeddingClient, Factory, _agentConfig, env,
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

    [Fact]
    public async Task RunAsync_indexes_unindexed_files()
    {
        var content = new string('x', 100);
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-14.md"), content);

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, result.FilesIndexed);
        Assert.Single(_store.GetIndexedPaths());
    }

    [Fact]
    public async Task RunAsync_skips_already_indexed_files()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-14.md"), new string('x', 100));
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-14.md",
            Summary = "Already indexed",
            Embedding = [0.1f, 0.2f, 0.3f],
            IndexedAt = DateTime.UtcNow
        });

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.FilesIndexed);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public async Task RunAsync_excludes_today_file()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        File.WriteAllText(Path.Combine(_memoryDir, $"{today}.md"), new string('x', 100));

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(0, result.FilesScanned);
    }

    [Fact]
    public async Task RunAsync_skips_short_files()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-14.md"), "Short");

        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(0, result.FilesIndexed);
        Assert.True(result.FilesSkipped > 0);
    }

    [Fact]
    public async Task RunAsync_empty_memory_dir_returns_zero()
    {
        var service = CreateService();
        var result = await service.RunAsync();

        Assert.Equal(0, result.FilesScanned);
        Assert.Equal(0, result.FilesIndexed);
    }

    [Fact]
    public async Task SearchAsync_returns_ranked_results()
    {
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-14.md",
            Summary = "About cooking",
            Embedding = [1.0f, 0.0f, 0.0f],
            IndexedAt = DateTime.UtcNow
        });
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-15.md",
            Summary = "About programming",
            Embedding = [0.0f, 1.0f, 0.0f],
            IndexedAt = DateTime.UtcNow
        });

        // Fake embedding returns [1,0,0] — should rank "cooking" highest
        var handler = new FakeEmbeddingHandler(
            """{"data":[{"embedding":[1.0,0.0,0.0],"index":0}]}""");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test/") };
        var embClient = new EmbeddingClient(httpClient, _agentConfig,
            NullLogger<EmbeddingClient>.Instance);

        var service = CreateService(embClient);
        var results = await service.SearchAsync("cooking", 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("memory/2026-04-14.md", results[0].FilePath);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void LoadFile_returns_content_for_valid_path()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-14.md"), "Hello world");
        var service = CreateService();

        var content = service.LoadFile("memory/2026-04-14.md");

        Assert.Equal("Hello world", content);
    }

    [Fact]
    public void LoadFile_rejects_path_traversal()
    {
        var service = CreateService();
        Assert.Null(service.LoadFile("../../../etc/passwd"));
        Assert.Null(service.LoadFile("memory/../../secret.txt"));
    }

    [Fact]
    public void LoadFile_rejects_absolute_path()
    {
        var service = CreateService();
        Assert.Null(service.LoadFile("/etc/passwd"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "MemoryIndexServiceTests" -v q
```

Expected: Compilation error — `MemoryIndexService` does not exist yet.

- [ ] **Step 3: Implement MemoryIndexService**

Create `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Result of an indexing run.
/// </summary>
public sealed record IndexResult
{
    [JsonPropertyName("filesScanned")]
    public int FilesScanned { get; init; }

    [JsonPropertyName("filesIndexed")]
    public int FilesIndexed { get; init; }

    [JsonPropertyName("filesSkipped")]
    public int FilesSkipped { get; init; }

    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// A single search result with relevance score.
/// </summary>
public sealed record SearchResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("score")]
    public required float Score { get; init; }
}

/// <summary>
/// Orchestrates memory indexing (scan, summarize, embed, store) and search.
/// </summary>
public sealed class MemoryIndexService
{
    private readonly MemoryIndexStore _store;
    private readonly EmbeddingClient _embeddingClient;
    private readonly Func<string, ILlmTextProvider> _providerFactory;
    private readonly AgentConfig _agentConfig;
    private readonly AgentEnvironment _environment;
    private readonly ILogger<MemoryIndexService> _logger;

    public MemoryIndexService(
        MemoryIndexStore store,
        EmbeddingClient embeddingClient,
        Func<string, ILlmTextProvider> providerFactory,
        AgentConfig agentConfig,
        AgentEnvironment environment,
        ILogger<MemoryIndexService> logger)
    {
        _store = store;
        _embeddingClient = embeddingClient;
        _providerFactory = providerFactory;
        _agentConfig = agentConfig;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Scans for unindexed daily logs, generates summaries and embeddings, stores in index.</summary>
    public async Task<IndexResult> RunAsync(CancellationToken ct = default)
    {
        var memoryDir = Path.Combine(_environment.DataPath, "memory");
        if (!Directory.Exists(memoryDir))
            return new IndexResult();

        var todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Scan for daily log files, exclude today (still being written)
        var allFiles = Directory.GetFiles(memoryDir, "????-??-??.md")
            .Where(f => Path.GetFileNameWithoutExtension(f) != todayUtc)
            .OrderBy(f => f)
            .ToList();

        // Filter out already-indexed files
        var indexedPaths = _store.GetIndexedPaths();
        var unindexed = allFiles
            .Where(f => !indexedPaths.Contains($"memory/{Path.GetFileName(f)}"))
            .ToList();

        int indexed = 0, skipped = 0;
        var errors = new List<string>();

        foreach (var filePath in unindexed)
        {
            try
            {
                var content = await File.ReadAllTextAsync(filePath, ct);
                if (content.Length < 50)
                {
                    skipped++;
                    continue;
                }

                var summary = await GenerateSummaryAsync(content, ct);
                var embedding = await _embeddingClient.GenerateEmbeddingAsync(summary, ct);
                var relativePath = $"memory/{Path.GetFileName(filePath)}";

                _store.Insert(new MemoryIndexEntry
                {
                    FilePath = relativePath,
                    Summary = summary,
                    Embedding = embedding,
                    IndexedAt = DateTime.UtcNow
                });

                indexed++;
                _logger.LogInformation("Indexed {FilePath}", relativePath);
            }
            catch (Exception ex)
            {
                var fileName = Path.GetFileName(filePath);
                errors.Add($"{fileName}: {ex.Message}");
                _logger.LogError(ex, "Failed to index {FilePath}", filePath);
            }
        }

        return new IndexResult
        {
            FilesScanned = allFiles.Count,
            FilesIndexed = indexed,
            FilesSkipped = allFiles.Count - unindexed.Count + skipped,
            Errors = errors
        };
    }

    /// <summary>Embeds the query and finds the most similar indexed entries.</summary>
    public async Task<List<SearchResult>> SearchAsync(string query, int limit = 5, CancellationToken ct = default)
    {
        var queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query, ct);
        var entries = _store.GetAllEntries();

        return entries
            .Select(e => new SearchResult
            {
                FilePath = e.FilePath,
                Summary = e.Summary,
                Score = MemoryIndexStore.CosineSimilarity(queryEmbedding, e.Embedding)
            })
            .OrderByDescending(r => r.Score)
            .Take(Math.Min(limit, 20))
            .ToList();
    }

    /// <summary>Returns aggregate index stats.</summary>
    public IndexStats GetStats() => _store.GetStats();

    /// <summary>Reads a file relative to dataPath. Returns null if path is invalid or file missing.</summary>
    public string? LoadFile(string relativePath)
    {
        if (relativePath.Contains("..") || Path.IsPathRooted(relativePath))
            return null;

        var fullPath = Path.GetFullPath(Path.Combine(_environment.DataPath, relativePath));
        var dataPath = Path.GetFullPath(_environment.DataPath);
        if (!fullPath.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }

    private async Task<string> GenerateSummaryAsync(string content, CancellationToken ct)
    {
        var provider = _providerFactory(_agentConfig.CompactionProvider);
        var messages = new List<Message>
        {
            new()
            {
                Id = "sys", ConversationId = "", Role = "system",
                Content = "Summarize this daily memory log in 2-3 sentences. Focus on key topics discussed, decisions made, and facts learned. Output JSON: { \"summary\": \"...\" }"
            },
            new()
            {
                Id = "usr", ConversationId = "", Role = "user",
                Content = content
            }
        };
        var options = new CompletionOptions { ResponseFormat = "json_object" };

        var fullContent = new StringBuilder();
        await foreach (var evt in provider.CompleteAsync(messages, _agentConfig.CompactionModel, options, ct))
        {
            if (evt is TextDelta delta)
                fullContent.Append(delta.Content);
        }

        using var doc = JsonDocument.Parse(fullContent.ToString());
        return doc.RootElement.GetProperty("summary").GetString()!;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "MemoryIndexServiceTests" -v q
```

Expected: All 9 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs \
        src/agent/OpenAgent.Tests/MemoryIndexServiceTests.cs
git commit -m "feat(memory): add MemoryIndexService for indexing orchestration and search"
```

---

## Task 5: MemoryToolHandler — Agent Tools

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
    private readonly MemoryIndexStore _store;
    private readonly MemoryIndexService _service;
    private readonly MemoryToolHandler _handler;
    private readonly EmbeddingClient _embeddingClient;

    public MemoryToolHandlerTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"memtool-test-{Guid.NewGuid()}");
        var memoryDir = Path.Combine(_dataDir, "memory");
        Directory.CreateDirectory(memoryDir);

        var dbPath = Path.Combine(_dataDir, "memory-index.db");
        _store = new MemoryIndexStore(dbPath, NullLogger<MemoryIndexStore>.Instance);

        var agentConfig = new AgentConfig
        {
            CompactionProvider = "fake",
            CompactionModel = "fake-model",
            EmbeddingDeployment = "test"
        };

        // Fake embedding: returns [1,0,0] for all queries
        var embHandler = new FakeEmbeddingHandler(
            """{"data":[{"embedding":[1.0,0.0,0.0],"index":0}]}""");
        var httpClient = new HttpClient(embHandler) { BaseAddress = new Uri("https://test/") };
        _embeddingClient = new EmbeddingClient(httpClient, agentConfig,
            NullLogger<EmbeddingClient>.Instance);

        var fakeProvider = new StreamingTextProvider("""{"summary":"Test"}""");
        ILlmTextProvider Factory(string _) => fakeProvider;
        var env = new AgentEnvironment { DataPath = _dataDir };

        _service = new MemoryIndexService(
            _store, _embeddingClient, Factory, agentConfig, env,
            NullLogger<MemoryIndexService>.Instance);
        _handler = new MemoryToolHandler(_service, _embeddingClient);
    }

    public void Dispose()
    {
        _embeddingClient.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    [Fact]
    public void Exposes_two_tools_when_configured()
    {
        Assert.Equal(2, _handler.Tools.Count);
        Assert.Contains(_handler.Tools, t => t.Definition.Name == "search_memory");
        Assert.Contains(_handler.Tools, t => t.Definition.Name == "load_memory_file");
    }

    [Fact]
    public void Exposes_no_tools_when_unconfigured()
    {
        var unconfiguredClient = new EmbeddingClient(new AgentConfig(),
            NullLogger<EmbeddingClient>.Instance);
        var handler = new MemoryToolHandler(_service, unconfiguredClient);

        Assert.Empty(handler.Tools);
    }

    [Fact]
    public async Task SearchMemory_returns_ranked_results()
    {
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-14.md",
            Summary = "Cooking recipes",
            Embedding = [1.0f, 0.0f, 0.0f],
            IndexedAt = DateTime.UtcNow
        });
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-15.md",
            Summary = "Programming tasks",
            Embedding = [0.0f, 1.0f, 0.0f],
            IndexedAt = DateTime.UtcNow
        });

        var tool = _handler.Tools.First(t => t.Definition.Name == "search_memory");
        var result = await tool.ExecuteAsync("""{"query":"cooking"}""", "test-conv");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");

        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("memory/2026-04-14.md", results[0].GetProperty("filePath").GetString());
    }

    [Fact]
    public async Task SearchMemory_respects_limit()
    {
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-14.md", Summary = "A", Embedding = [1f, 0f, 0f],
            IndexedAt = DateTime.UtcNow
        });
        _store.Insert(new MemoryIndexEntry
        {
            FilePath = "memory/2026-04-15.md", Summary = "B", Embedding = [0f, 1f, 0f],
            IndexedAt = DateTime.UtcNow
        });

        var tool = _handler.Tools.First(t => t.Definition.Name == "search_memory");
        var result = await tool.ExecuteAsync("""{"query":"test","limit":1}""", "test-conv");
        var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task LoadMemoryFile_returns_file_content()
    {
        File.WriteAllText(Path.Combine(_dataDir, "memory", "2026-04-14.md"), "Day contents here");

        var tool = _handler.Tools.First(t => t.Definition.Name == "load_memory_file");
        var result = await tool.ExecuteAsync("""{"file_path":"memory/2026-04-14.md"}""", "test-conv");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("Day contents here", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task LoadMemoryFile_rejects_traversal()
    {
        var tool = _handler.Tools.First(t => t.Definition.Name == "load_memory_file");
        var result = await tool.ExecuteAsync("""{"file_path":"../../../etc/passwd"}""", "test-conv");
        var doc = JsonDocument.Parse(result);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd src/agent && dotnet test --filter "MemoryToolHandlerTests" -v q
```

Expected: Compilation error — `MemoryToolHandler` does not exist yet.

- [ ] **Step 3: Implement MemoryToolHandler**

Create `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs`:

```csharp
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Provides memory search and file loading tools to the agent.
/// Tools are only exposed when embedding config is present.
/// </summary>
public sealed class MemoryToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public MemoryToolHandler(MemoryIndexService service, EmbeddingClient embeddingClient)
    {
        Tools = embeddingClient.IsConfigured
            ? [new SearchMemoryTool(service), new LoadMemoryFileTool(service)]
            : [];
    }
}

/// <summary>
/// Searches the memory index for past daily logs matching a natural language query.
/// </summary>
internal sealed class SearchMemoryTool(MemoryIndexService service) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "search_memory",
        Description = "Search the memory index for past daily logs matching a query. Returns summaries and file paths of the most relevant entries.",
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

/// <summary>
/// Loads the full contents of a daily memory log file.
/// </summary>
internal sealed class LoadMemoryFileTool(MemoryIndexService service) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "load_memory_file",
        Description = "Load the full contents of a daily memory log file. Use after search_memory to get details.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                file_path = new { type = "string", description = "Relative path from search results, e.g. memory/2026-04-15.md" }
            },
            required = new[] { "file_path" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            var filePath = args.GetProperty("file_path").GetString()
                ?? throw new ArgumentException("file_path is required");

            var content = service.LoadFile(filePath);
            if (content is null)
                return Task.FromResult(JsonSerializer.Serialize(new { error = "file not found or path not allowed" }));

            return Task.FromResult(JsonSerializer.Serialize(new { file_path = filePath, content }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/agent && dotnet test --filter "MemoryToolHandlerTests" -v q
```

Expected: All 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs \
        src/agent/OpenAgent.Tests/MemoryToolHandlerTests.cs
git commit -m "feat(memory): add search_memory and load_memory_file agent tools"
```

---

## Task 6: MemoryIndexHostedService + Endpoints + DI Wiring

**Files:**
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs`
- Create: `src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs`
- Create: `src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs`
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
            try
            {
                await CheckAndRunAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory index timer tick failed");
            }
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
            "Memory index complete: scanned={Scanned}, indexed={Indexed}, skipped={Skipped}, errors={Errors}",
            result.FilesScanned, result.FilesIndexed, result.FilesSkipped, result.Errors.Count);
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

- [ ] **Step 3: Implement the DI extensions**

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
    /// <summary>
    /// Registers memory index store, embedding client, service, tools, and hosted service.
    /// </summary>
    public static IServiceCollection AddMemoryIndex(this IServiceCollection services)
    {
        services.AddSingleton<MemoryIndexStore>();
        services.AddSingleton<EmbeddingClient>();
        services.AddSingleton<MemoryIndexService>();
        services.AddSingleton<IToolHandler, MemoryToolHandler>();
        services.AddHostedService<MemoryIndexHostedService>();
        return services;
    }
}
```

- [ ] **Step 4: Wire into Program.cs**

In `src/agent/OpenAgent/Program.cs`:

Add the using statement at the top with the other usings:

```csharp
using OpenAgent.MemoryIndex;
```

Add the service registration after the existing `builder.Services.AddScheduledTasks(environment.DataPath);` line (around line 84):

```csharp
builder.Services.AddMemoryIndex();
```

Add the endpoint mapping after the existing `app.MapToolEndpoints();` line (around line 190):

```csharp
app.MapMemoryIndexEndpoints();
```

- [ ] **Step 5: Build the full solution**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds with no errors.

- [ ] **Step 6: Run all tests**

```bash
cd src/agent && dotnet test -v q
```

Expected: All tests pass (existing tests unchanged, new tests pass).

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs \
        src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs \
        src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs \
        src/agent/OpenAgent/Program.cs
git commit -m "feat(memory): add hosted service, endpoints, and DI wiring for memory index"
```

---

## Spec Coverage Check

| Spec Section | Task |
|---|---|
| New project: OpenAgent.MemoryIndex | Task 1 |
| AgentConfig additions (4 fields) | Task 1, Step 6 |
| SQLite schema + persistence | Task 2 |
| Embedding serialization/deserialization | Task 2 |
| Cosine similarity | Task 2 |
| Azure OpenAI Embeddings API client | Task 3 |
| Indexing flow (scan, summarize, embed, store) | Task 4 |
| Search flow (embed query, similarity, rank) | Task 4 |
| Summary generation via CompactionProvider | Task 4 |
| search_memory tool | Task 5 |
| load_memory_file tool | Task 5 |
| Path traversal protection | Task 4, Task 5 |
| Graceful degradation (empty config) | Task 5 (no tools exposed), Task 6 (hosted service warns) |
| IHostedService with daily timer | Task 6 |
| REST endpoints (run, stats) | Task 6 |
| DI registration | Task 6 |
| Program.cs wiring | Task 6 |
| Modified files (csproj, sln, Program.cs) | Task 1, Task 6 |
| Unit tests (store, client, service, tools) | Tasks 2-5 |
| Error handling (skip and continue) | Task 4 |
