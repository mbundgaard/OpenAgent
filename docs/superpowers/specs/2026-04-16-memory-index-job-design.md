# Memory Index Job — Design Spec

Issue: #17 | Depends on: nothing | Blocks: #19 (Digest), #51 (Background)

---

## Purpose

Make old daily memory logs searchable at the topic level. When a daily log file passes the memory window threshold, the nightly job chunks it into self-contained topics via an LLM, embeds each chunk, and stores everything in `memory.db`. The source file is then deleted — the database is the source of truth for all processed days.

This is step 1 of the three-job memory system described in [docs/memory/DESIGN.md](../../memory/DESIGN.md).

---

## Memory Lifecycle

```
Today's file (on disk)          Recent files (on disk)          Processed (in DB)
memory/2026-04-18.md            memory/2026-04-17.md            memory.db
                                memory/2026-04-16.md
Agent writes raw notes  ───>    Loaded into system prompt  ───>  Searchable via search_memory
via file tools                  (3-day window, configurable)     File deleted from disk
```

- **Within `memoryDays` window** (default 3 days): Files live on disk. `SystemPromptBuilder` loads them into the system prompt. No change to existing behavior.
- **Past the window:** Nightly job processes the file into chunks in `memory.db`, then deletes the file. Agent accesses via `search_memory` tool.
- **No overlap:** A day's content is either on disk (recent) or in the DB (processed). Never both.
- **Nothing from `memory.db` is preloaded into the system prompt.** The agent uses the `search_memory` tool.

---

## Approach: In-memory Cosine Similarity

Store embeddings as BLOBs in SQLite. Load all chunks into memory for similarity search — pure C#, no native dependencies.

Daily logs produce ~3-10 chunks each. At 365 days/year, that's a few thousand chunks after years. At 1536 dims x 4 bytes = 6KB per embedding, even 5000 chunks = 30MB. Trivially fits in memory.

If this ever needs to scale, sqlite-vec is a drop-in upgrade. External vector stores (Qdrant, Pinecone) are massive overkill.

---

## Data Model

### SQLite database: `{dataPath}/memory.db`

```sql
CREATE TABLE IF NOT EXISTS memory_chunks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    date        TEXT NOT NULL,
    chunk_index INTEGER NOT NULL,
    content     TEXT NOT NULL,
    embedding   BLOB NOT NULL,
    UNIQUE(date, chunk_index)
);
```

| Column | Type | Description |
|--------|------|-------------|
| `date` | TEXT | Day the chunk came from, e.g. `2026-04-17` |
| `chunk_index` | INTEGER | Position within the day (0-based) |
| `content` | TEXT | The chunk text — self-contained topic |
| `embedding` | BLOB | `float[1536]` as little-endian bytes |

No `memory_days` tracking table. If a `.md` file exists on disk and no chunks exist for that date, it needs processing.

### Embedding serialization

```csharp
static byte[] Serialize(float[] embedding)
{
    var bytes = new byte[embedding.Length * sizeof(float)];
    Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
    return bytes;
}

static float[] Deserialize(byte[] bytes)
{
    var embedding = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
    return embedding;
}
```

---

## Indexing Flow

Triggered nightly by the hosted service, or manually via endpoint.

1. **Scan** — `Directory.GetFiles(memoryDir, "????-??-??.md")`, exclude files within the `memoryDays` window (default 3 days including today). A file dated 3+ days ago is eligible for processing. This preserves the system prompt's recent memory window.
2. **Filter** — Query `memory_chunks` for distinct dates, skip files whose date already has chunks
3. **For each unprocessed file** (oldest first):
   a. Read file content
   b. Skip if empty or trivially short (< 50 chars)
   c. **LLM call:** Chunk the file into self-contained topics (JSON array)
   d. **Embedding calls:** Generate embedding for each chunk
   e. Insert all chunks into `memory_chunks`
   f. **Delete the source file** from disk
   g. Log success
4. **On error** — log, skip the file, continue to next. File stays on disk for retry.
5. **Return** — `IndexResult { FilesScanned, FilesProcessed, ChunksCreated, Errors }`

### LLM chunking

Single LLM call per file. Uses `compactionProvider` / `compactionModel`.

**System prompt:**

```
Restructure this daily memory log into self-contained topic chunks.

Rules:
- Each chunk covers one topic or conversation thread
- Each chunk must be understandable on its own, without the other chunks
- Preserve all factual information — names, dates, decisions, URLs
- Don't add information that wasn't in the original
- Don't merge unrelated topics into one chunk
- If the entire file is one topic, return a single chunk

Output JSON: { "chunks": ["chunk text 1", "chunk text 2", ...] }
```

**Response format:** `json_object`

**User message:** Full file content

**Parse:** Extract `chunks` string array from JSON response.

### Embedding

Embed the **chunk content directly** — no summary layer. Chunks are already concise and topically focused. One Azure OpenAI Embeddings API call per chunk.

```
POST {endpoint}/openai/deployments/{deployment}/embeddings?api-version=2024-02-01
Headers: api-key: {key}, Content-Type: application/json
Body: { "input": "{chunk text}" }
Response: { "data": [{ "embedding": [0.1, 0.2, ...] }] }
```

---

## Search Flow

Used by the `search_memory` tool during agent sessions.

1. Agent calls `search_memory(query)`
2. Generate embedding for query via `EmbeddingClient`
3. Load all chunks from `memory_chunks` (cached in service, invalidated on index run)
4. Compute cosine similarity between query embedding and each chunk embedding
5. Sort by similarity descending, take top N (default 5)
6. Return results with: date, chunk content, score

The agent gets the actual content — no follow-up load needed.

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
    var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
    return denom == 0 ? 0 : dot / denom;
}
```

---

## Configuration

### AgentConfig additions

Four new flat fields in `AgentConfig` (stored in `{dataPath}/config/agent.json`):

| Field | Description | Default |
|-------|-------------|---------|
| `embeddingEndpoint` | Azure OpenAI endpoint URL | `""` (disabled) |
| `embeddingApiKey` | API key for embedding calls | `""` |
| `embeddingDeployment` | Deployment name (e.g. `text-embedding-3-small`) | `""` |
| `indexRunAtHour` | Hour (UTC) to run the nightly job | `2` |

Chunking uses `compactionProvider` and `compactionModel`.

**Graceful degradation:** If `embeddingEndpoint` is empty, the hosted service logs a warning and skips. The `search_memory` tool is not exposed.

---

## Tool

### search_memory

```json
{
  "name": "search_memory",
  "description": "Search past memories by topic. Returns the most relevant chunks from daily logs with their content and date.",
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

**Returns:** JSON array of `{ date, content, score }` sorted by relevance descending.

No `load_memory_file` tool — the agent already has `file_read` for any on-disk files. Search results include the chunk content directly, so no follow-up load is needed for processed days.

---

## Architecture

### New project: `OpenAgent.MemoryIndex`

```
src/agent/OpenAgent.MemoryIndex/
├── OpenAgent.MemoryIndex.csproj
├── MemoryChunkStore.cs           — SQLite persistence for chunks
├── EmbeddingClient.cs            — Azure OpenAI Embeddings API client
├── MemoryChunker.cs              — LLM call that splits a file into topic chunks
├── MemoryIndexService.cs         — Orchestrates indexing and search
├── MemoryToolHandler.cs          — IToolHandler: search_memory
├── MemoryIndexHostedService.cs   — IHostedService with daily timer
├── MemoryIndexEndpoints.cs       — REST API for manual trigger and stats
└── ServiceCollectionExtensions.cs
```

### Component relationships

```
MemoryIndexHostedService (timer)
    └── MemoryIndexService (orchestration)
            ├── MemoryChunkStore (SQLite persistence)
            ├── MemoryChunker (LLM chunking via Func<string, ILlmTextProvider>)
            └── EmbeddingClient (Azure OpenAI embeddings)

MemoryToolHandler (agent tool)
    └── MemoryIndexService (search)

MemoryIndexEndpoints (REST API)
    └── MemoryIndexService (manual trigger, stats)
```

### Dependencies

- `OpenAgent.Contracts` — AgentEnvironment, ILlmTextProvider, ITool, IToolHandler
- `OpenAgent.Models` — Message, CompletionEvent, CompletionOptions, AgentConfig
- `Microsoft.Data.Sqlite` — already in Directory.Packages.props
- `Microsoft.Extensions.Hosting.Abstractions` — already in Directory.Packages.props
- `Microsoft.Extensions.Logging.Abstractions` — already in Directory.Packages.props

No new NuGet packages required.

---

## Hosted Service

### MemoryIndexHostedService

- **Timer:** Checks every hour via `PeriodicTimer`
- **Guard:** Skips if already ran today (`_lastRunDate == today`)
- **Guard:** Skips if current hour (UTC) < `indexRunAtHour`
- **Guard:** Skips if embedding config is missing (logs warning once)
- **Runs:** `MemoryIndexService.RunAsync()` — awaitable, returns `IndexResult`
- **Error handling:** Catches all exceptions, logs error, allows retry tomorrow

---

## Endpoints

### POST /api/memory-index/run

Triggers indexing manually.

**Response:**
```json
{
  "filesScanned": 5,
  "filesProcessed": 2,
  "chunksCreated": 7,
  "errors": []
}
```

### GET /api/memory-index/stats

**Response:**
```json
{
  "totalChunks": 142,
  "totalDays": 38,
  "oldestDate": "2026-02-01",
  "newestDate": "2026-04-15"
}
```

Both require authorization.

---

## Testing

### Unit tests

| Test | What it verifies |
|------|-----------------|
| `MemoryChunkStore_InsertAndQuery` | Insert chunks, query by date, verify roundtrip |
| `MemoryChunkStore_DuplicateChunk` | UNIQUE(date, chunk_index) prevents duplicates |
| `MemoryChunkStore_EmbeddingRoundtrip` | float[] → BLOB → float[] preserves values |
| `MemoryChunkStore_GetProcessedDates` | Returns distinct dates that have chunks |
| `MemoryChunkStore_GetAllChunks` | Bulk load for similarity search |
| `EmbeddingClient_ParsesResponse` | Extracts float[] from API response |
| `EmbeddingClient_FormatsRequest` | HTTP request matches Azure spec |
| `MemoryChunker_ParsesChunks` | Extracts string array from LLM JSON response |
| `MemoryIndexService_ProcessesUnindexedFiles` | Full flow: chunk → embed → store → delete |
| `MemoryIndexService_SkipsAlreadyProcessed` | Files with chunks in DB are skipped |
| `MemoryIndexService_SkipsTodayFile` | Today excluded from scan |
| `MemoryIndexService_SkipsShortFiles` | Files under 50 chars skipped |
| `MemoryIndexService_SearchReturnsRanked` | Correct ranking and limit enforcement |
| `MemoryIndexService_FileDeletedAfterProcessing` | Source file removed from disk |
| `CosineSimilarity_KnownVectors` | Verified against known expected scores |
| `HostedService_SkipsWhenNotConfigured` | No crash, logs warning |
| `HostedService_RunsOncePerDay` | Second call same day is a no-op |
| `SearchMemoryTool_ReturnsContent` | Returns chunk text, not just pointers |

---

## Modified Files

| File | Change |
|------|--------|
| `OpenAgent.Models/Configs/AgentConfig.cs` | Add 4 embedding/index config fields |
| `OpenAgent/OpenAgent.csproj` | Add ProjectReference to OpenAgent.MemoryIndex |
| `OpenAgent/Program.cs` | Add `AddMemoryIndex()` + `MapMemoryIndexEndpoints()` |
| `OpenAgent.Tests/OpenAgent.Tests.csproj` | Add ProjectReference to OpenAgent.MemoryIndex |
| `OpenAgent.sln` | Add OpenAgent.MemoryIndex project |

---

## Not in Scope

- **Digest job** (#19) — separate issue, curates MEMORY.md
- **Background job** (#51) — separate issue, autonomous agent runs
- **Preloading chunks into system prompt** — the agent uses search_memory
- **Re-chunking already processed files** — if needed later, add a "reprocess" endpoint
- **Settings UI for embedding config** — configure via agent.json for now
