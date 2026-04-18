# Memory Index Job — Design Spec

Issue: #17 | Depends on: nothing | Blocks: #19 (Digest), #51 (Background)

---

## Purpose

Index closed daily memory logs (`memory/YYYY-MM-DD.md`) into a searchable vector store. Makes old memories findable via `search_memory` and `load_memory_file` agent tools. This is step 1 of the three-job memory system described in [docs/memory/DESIGN.md](../../memory/DESIGN.md).

---

## Approaches Considered

### A. In-memory cosine similarity (recommended)

Store embeddings as BLOBs in SQLite. Load all embeddings into memory for similarity search — pure C#, no native dependencies.

- **Pro:** Simple, no native deps, trivially fast at this scale, easy Docker deployment
- **Con:** Doesn't scale past tens of thousands of entries
- **Scale reality:** Daily logs cap at ~365/year. 3 years = ~1000 entries. At 1536 dims × 4 bytes = 6KB each, that's 6MB in memory. Microsecond search times.

### B. sqlite-vec native extension

Use the sqlite-vec C extension for SQL-level vector operations.

- **Pro:** SQL-based similarity queries, scales to millions
- **Con:** Native binary per platform (.dll/.so/.dylib), Docker build complexity, immature .NET bindings, zero practical benefit at our scale

### C. External vector store (Qdrant, Pinecone, etc.)

Managed vector database service.

- **Pro:** Infinite scale, managed infrastructure
- **Con:** External dependency, network latency, cost, massive overkill for hundreds of entries

**Decision: Approach A.** The dataset is tiny and always will be (one entry per day). In-memory cosine similarity is the right tool. If this ever needs to scale (it won't), sqlite-vec is a drop-in upgrade.

---

## Architecture

### New project: `OpenAgent.MemoryIndex`

```
src/agent/OpenAgent.MemoryIndex/
├── OpenAgent.MemoryIndex.csproj
├── MemoryIndexStore.cs          — SQLite persistence, CRUD, embedding serialization
├── EmbeddingClient.cs           — Azure OpenAI Embeddings API client
├── MemoryIndexService.cs        — Orchestrates indexing: scan → summarize → embed → store
├── MemoryIndexHostedService.cs  — IHostedService with daily timer
├── MemoryToolHandler.cs         — IToolHandler: search_memory, load_memory_file
├── MemoryIndexEndpoints.cs      — REST API for manual trigger and stats
└── ServiceCollectionExtensions.cs
```

### Component relationships

```
MemoryIndexHostedService (timer)
    └── MemoryIndexService (orchestration)
            ├── MemoryIndexStore (SQLite persistence)
            ├── EmbeddingClient (Azure OpenAI embeddings)
            └── ILlmTextProvider (summary generation via Func<string, ILlmTextProvider>)

MemoryToolHandler (agent tools)
    └── MemoryIndexService (search, file loading)

MemoryIndexEndpoints (REST API)
    └── MemoryIndexService (manual trigger, stats)
```

### Dependencies

- `OpenAgent.Contracts` — AgentEnvironment, ILlmTextProvider, ITool, IToolHandler
- `OpenAgent.Models` — Message, CompletionEvent, CompletionOptions, AgentConfig
- `Microsoft.Data.Sqlite` — database access (already in Directory.Packages.props)
- `Microsoft.Extensions.Hosting.Abstractions` — IHostedService (already in Directory.Packages.props)
- `Microsoft.Extensions.Logging.Abstractions` — logging (already in Directory.Packages.props)
No new NuGet packages required. EmbeddingClient creates and manages its own `HttpClient`, consistent with how `AzureOpenAiTextProvider` works.

---

## Data Model

### SQLite database: `{dataPath}/memory-index.db`

Separate from `conversations.db`. The memory index is derived data — can always be rebuilt from source files. Separate DB means independent schema, easy delete-and-rebuild, no migration conflicts.

```sql
CREATE TABLE IF NOT EXISTS memory_index (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path   TEXT NOT NULL UNIQUE,
    summary     TEXT NOT NULL,
    embedding   BLOB NOT NULL,
    indexed_at  TEXT NOT NULL
);
```

| Column | Type | Description |
|--------|------|-------------|
| `file_path` | TEXT UNIQUE | Relative path from dataPath, e.g. `memory/2026-04-15.md` |
| `summary` | TEXT | LLM-generated 2-3 sentence summary |
| `embedding` | BLOB | `float[1536]` as little-endian bytes (6144 bytes) |
| `indexed_at` | TEXT | ISO 8601 timestamp |

### Embedding serialization

```csharp
// float[] → byte[]
static byte[] Serialize(float[] embedding)
{
    var bytes = new byte[embedding.Length * sizeof(float)];
    Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
    return bytes;
}

// byte[] → float[]
static float[] Deserialize(byte[] bytes)
{
    var embedding = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
    return embedding;
}
```

---

## Indexing Flow

Triggered nightly by the hosted service, or manually via endpoint/tool.

1. **Scan** — `Directory.GetFiles(memoryDir, "????-??-??.md")`, parse dates, exclude today (UTC date — conservative; a file from late Copenhagen evening gets indexed next run at most)
2. **Filter** — Query `memory_index` for existing `file_path` values, compute the set difference
3. **For each unindexed file** (oldest first):
   a. Read file content
   b. Skip if empty or trivially short (< 50 chars)
   c. Generate summary via LLM (single call)
   d. Generate embedding via Azure OpenAI Embeddings API
   e. Insert into `memory_index` table
   f. Log success
4. **On error** — log, skip the file, continue to next. File will be retried on the next run.
5. **Return** — `IndexResult { FilesScanned, FilesIndexed, FilesSkipped, Errors }`

### Summary generation

Follows the CompactionSummarizer pattern:

- **Provider:** `Func<string, ILlmTextProvider>` resolved by `agentConfig.CompactionProvider`
- **Model:** `agentConfig.CompactionModel`
- **Options:** `CompletionOptions { ResponseFormat = "json_object" }`
- **System prompt:** "Summarize this daily memory log in 2-3 sentences. Focus on key topics discussed, decisions made, and facts learned. Output JSON: { \"summary\": \"...\" }"
- **User message:** Full file content
- **Parse:** Extract `summary` string from JSON response

Reuses compaction provider/model because it's the same class of work: internal, non-user-facing, single-shot LLM calls. If separate config is ever needed, add `indexProvider`/`indexModel` fields to AgentConfig at that point.

### Embedding generation

Embed the **summary**, not the full file content. Summaries are concise and capture semantic essence. Reduces embedding token costs.

```
POST {endpoint}/openai/deployments/{deployment}/embeddings?api-version=2024-02-01
Headers: api-key: {key}, Content-Type: application/json
Body: { "input": "{summary text}" }
Response: { "data": [{ "embedding": [0.1, 0.2, ...], "index": 0 }] }
```

---

## Search Flow

Used by the `search_memory` tool during agent sessions.

1. Receive query string from agent
2. Generate embedding for query via `EmbeddingClient`
3. Load ALL rows from `memory_index` (file_path, summary, embedding)
4. Compute cosine similarity between query embedding and each stored embedding
5. Sort by similarity descending, take top N (default 5)
6. Return results: `{ file_path, summary, score }`

Entries are cached in `MemoryIndexService` after first load. Cache invalidated when `RunAsync` indexes new files.

### Cosine similarity

```csharp
static float CosineSimilarity(float[] a, float[] b)
{
    float dot = 0, normA = 0, normB = 0;
    for (int i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        normA += a[i] * a[i];
        normB += b[i] * b[i];
    }
    return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
}
```

---

## Configuration

### AgentConfig additions

Four new flat fields in `AgentConfig` (stored in `{dataPath}/config/agent.json`):

```csharp
[JsonPropertyName("embeddingEndpoint")]
public string EmbeddingEndpoint { get; set; } = "";

[JsonPropertyName("embeddingApiKey")]
public string EmbeddingApiKey { get; set; } = "";

[JsonPropertyName("embeddingDeployment")]
public string EmbeddingDeployment { get; set; } = "";

[JsonPropertyName("indexRunAtHour")]
public int IndexRunAtHour { get; set; } = 2;
```

| Field | Description | Default |
|-------|-------------|---------|
| `embeddingEndpoint` | Azure OpenAI endpoint URL | `""` (disabled) |
| `embeddingApiKey` | API key for embedding calls | `""` |
| `embeddingDeployment` | Deployment name (e.g. `text-embedding-3-small`) | `""` |
| `indexRunAtHour` | Hour (UTC) to run the nightly job | `2` |

Summary generation reuses `compactionProvider` and `compactionModel`.

**Graceful degradation:** If `embeddingEndpoint` is empty, the hosted service logs a warning and skips. Tools return a clear message: "Memory index not configured — set embeddingEndpoint in agent config."

---

## Tools

### search_memory

```json
{
  "name": "search_memory",
  "description": "Search the memory index for past daily logs matching a query. Returns summaries and file paths of the most relevant entries.",
  "parameters": {
    "type": "object",
    "properties": {
      "query": {
        "type": "string",
        "description": "Natural language search query"
      },
      "limit": {
        "type": "integer",
        "description": "Maximum results to return (default 5, max 20)"
      }
    },
    "required": ["query"]
  }
}
```

**Returns:** JSON array of `{ filePath, summary, score }` sorted by relevance descending.

### load_memory_file

```json
{
  "name": "load_memory_file",
  "description": "Load the full contents of a daily memory log file. Use after search_memory to get details.",
  "parameters": {
    "type": "object",
    "properties": {
      "file_path": {
        "type": "string",
        "description": "Relative path from search results, e.g. memory/2026-04-15.md"
      }
    },
    "required": ["file_path"]
  }
}
```

**Returns:** Full file content as text. Path validated and scoped to `{dataPath}` — no traversal.

---

## Hosted Service

### MemoryIndexHostedService

```csharp
public sealed class MemoryIndexHostedService : IHostedService, IDisposable
```

- **Timer:** Checks every hour via `PeriodicTimer`
- **Guard:** Skips if already ran today (`_lastRunDate == today`)
- **Guard:** Skips if current hour (UTC) < `indexRunAtHour`
- **Guard:** Skips if embedding config is missing (logs warning once)
- **Runs:** `MemoryIndexService.RunAsync()` — awaitable, returns `IndexResult`
- **Error handling:** Catches all exceptions, logs error, resets so it can retry tomorrow

The `RunAsync` method is public and awaitable — the future digest job (#19) can chain after it.

---

## Endpoints

### POST /api/memory-index/run

Triggers indexing manually. Useful for testing and after bulk-importing daily logs.

**Response:**
```json
{
  "filesScanned": 45,
  "filesIndexed": 3,
  "filesSkipped": 42,
  "errors": []
}
```

### GET /api/memory-index/stats

**Response:**
```json
{
  "totalIndexed": 45,
  "lastIndexedAt": "2026-04-16T02:15:00Z",
  "oldestEntry": "memory/2026-02-01.md",
  "newestEntry": "memory/2026-04-15.md"
}
```

Both endpoints require authorization (consistent with all other API endpoints).

---

## DI Registration

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryIndex(this IServiceCollection services)
    {
        services.AddSingleton<MemoryIndexStore>();
        services.AddSingleton<EmbeddingClient>();
        services.AddSingleton<MemoryIndexService>();
        services.AddSingleton<IToolHandler, MemoryToolHandler>();
        services.AddHostedService<MemoryIndexHostedService>();
        return services;
    }

    public static WebApplication MapMemoryIndexEndpoints(this WebApplication app)
    {
        // Extension method on WebApplication, consistent with other endpoint registrations
        MemoryIndexEndpoints.Map(app);
        return app;
    }
}
```

In `Program.cs`:
```csharp
builder.Services.AddMemoryIndex();
// ... after app.Build()
app.MapMemoryIndexEndpoints();
```

---

## Testing

### Unit tests (in OpenAgent.Tests)

| Test | What it verifies |
|------|-----------------|
| `MemoryIndexStore_InsertAndQuery` | Insert row, query by file_path, verify roundtrip |
| `MemoryIndexStore_DuplicateFilePath` | UNIQUE constraint prevents double-indexing |
| `MemoryIndexStore_EmbeddingRoundtrip` | float[] → BLOB → float[] preserves values exactly |
| `MemoryIndexStore_GetAllEmbeddings` | Bulk load for similarity search |
| `EmbeddingClient_FormatsRequest` | HTTP request body matches Azure OpenAI spec |
| `EmbeddingClient_ParsesResponse` | Extracts float[] from API response |
| `MemoryIndexService_SkipsAlreadyIndexed` | Files in index are not re-processed |
| `MemoryIndexService_SkipsTodayFile` | Today's date is excluded from scan |
| `MemoryIndexService_SkipsEmptyFiles` | Files under 50 chars are skipped |
| `MemoryIndexService_HandlesLlmError` | Continues to next file on LLM failure |
| `CosineSimilarity_KnownVectors` | Verified against known expected scores |
| `SearchMemoryTool_ReturnsTopN` | Correct ranking and limit enforcement |
| `LoadMemoryFileTool_PathTraversal` | Rejects `../` and absolute paths |

### Integration tests

| Test | What it verifies |
|------|-----------------|
| `FullIndexingFlow` | Write test files → run index → verify DB rows |
| `SearchAfterIndex` | Index files → search → verify ranking makes sense |
| `ManualRunEndpoint` | POST /api/memory-index/run returns correct counts |

---

## Modified Files

| File | Change |
|------|--------|
| `OpenAgent.Models/Configs/AgentConfig.cs` | Add 4 embedding/index config fields |
| `OpenAgent/OpenAgent.csproj` | Add ProjectReference to OpenAgent.MemoryIndex |
| `OpenAgent/Program.cs` | Add `AddMemoryIndex()` + `MapMemoryIndexEndpoints()` |
| `OpenAgent.sln` | Add OpenAgent.MemoryIndex project |

---

## Not in Scope

- **Digest job** (#19) — separate issue, runs after index, curates MEMORY.md
- **Background job** (#51) — separate issue, autonomous agent runs
- **Chunk-level retrieval** — whole-file retrieval is sufficient; add chunking only if context bloat becomes real
- **Re-indexing changed files** — daily logs are immutable once closed; if needed, expose a "reindex" endpoint that deletes and recreates entries
- **Settings UI for embedding config** — configure via agent.json for now; UI can be added when other config fields get UI treatment
