# Memory Index Job — Design Spec

Issue: #17 | Depends on: nothing | Blocks: #19 (Digest), #51 (Background)

---

## Purpose

Make old daily memory logs searchable at the topic level via hybrid search (vector + keyword). When a daily log file passes the memory window, the nightly job chunks it into self-contained topics via an LLM (with a summary per chunk), embeds each chunk, indexes it for full-text search, and stores everything in `memory.db`. The source file is then deleted.

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
- **Nothing from `memory.db` is preloaded into the system prompt.** The agent uses tools.

---

## Data Model

### SQLite database: `{dataPath}/memory.db`

```sql
CREATE TABLE IF NOT EXISTS memory_chunks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    date        TEXT NOT NULL,
    chunk_index INTEGER NOT NULL,
    content     TEXT NOT NULL,
    summary     TEXT NOT NULL,
    embedding   BLOB NOT NULL,
    UNIQUE(date, chunk_index)
);

-- FTS5 for keyword search — indexes both summary and content
CREATE VIRTUAL TABLE IF NOT EXISTS memory_chunks_fts USING fts5(
    summary, content, content=memory_chunks, content_rowid=id
);
```

| Column | Type | Description |
|--------|------|-------------|
| `date` | TEXT | Day the chunk came from, e.g. `2026-04-17` |
| `chunk_index` | INTEGER | Position within the day (0-based) |
| `content` | TEXT | Full chunk text — self-contained topic |
| `summary` | TEXT | One-line summary of the chunk |
| `embedding` | BLOB | `float[1536]` as little-endian bytes |

The FTS5 table is an external-content table backed by `memory_chunks`. Kept in sync by inserting into both tables in the same transaction during indexing.

---

## Indexing Flow

Triggered nightly by the hosted service, or manually via endpoint.

1. **Scan** — `Directory.GetFiles(memoryDir, "????-??-??.md")`, exclude files within the `memoryDays` window. A file dated 3+ days ago is eligible.
2. **Filter** — Query `memory_chunks` for distinct dates, skip files whose date already has chunks.
3. **For each unprocessed file** (oldest first):
   a. Read file content
   b. Skip if empty or trivially short (< 50 chars)
   c. **LLM call:** Chunk the file into topics, each with content + summary
   d. **Embedding calls:** Generate embedding for each chunk's content
   e. Insert all chunks into `memory_chunks` + `memory_chunks_fts` (one transaction)
   f. **Delete the source file** from disk
   g. Log success
4. **On error** — log, skip the file, continue to next. File stays on disk for retry.
5. **Return** — `IndexResult { FilesScanned, FilesProcessed, ChunksCreated, Errors }`

### LLM chunking

Single LLM call per file. Uses `compactionProvider` / `compactionModel`.

**System prompt:**

```
Restructure this daily memory log into self-contained topic chunks. For each chunk, provide the full content and a one-line summary.

Rules:
- Each chunk covers one topic or conversation thread
- Each chunk must be understandable on its own, without the other chunks
- The summary is a single sentence capturing the chunk's essence
- Preserve all factual information — names, dates, decisions, URLs
- Don't add information that wasn't in the original
- Don't merge unrelated topics into one chunk
- If the entire file is one topic, return a single chunk

Output JSON:
{
  "chunks": [
    { "content": "full chunk text", "summary": "one-line summary" },
    ...
  ]
}
```

**Response format:** `json_object`

### Embedding

Embed the **chunk content directly**. One Azure OpenAI Embeddings API call per chunk.

```
POST {endpoint}/openai/deployments/{deployment}/embeddings?api-version=2024-02-01
Headers: api-key: {key}, Content-Type: application/json
Body: { "input": "{chunk content}" }
```

---

## Search Flow — Hybrid (Vector + Keyword)

Two ranking signals combined with configurable weights.

### 1. Vector search

Cosine similarity between query embedding and all chunk embeddings. Scores range 0..1. Captures semantic meaning — "auth" matches "authentication middleware."

### 2. Keyword search

SQLite FTS5 `MATCH` against summary and content. Returns BM25 rank scores. Captures exact terms that embeddings might dilute — "JIRA-1234", specific names, URLs.

### 3. Combined scoring

```
final_score = (vector_weight * cosine_score) + (keyword_weight * normalized_fts_score)
```

- `vector_weight = 0.7`, `keyword_weight = 0.3` (constants, tunable later)
- FTS5 rank is negative (more negative = better). Normalize: `1 / (1 + abs(rank))`
- Chunks not found in FTS results get `normalized_fts_score = 0`

### Flow

1. Agent calls `search_memory(query)`
2. Generate embedding for query → cosine similarity against all cached chunks
3. Run FTS5 MATCH query → get matching chunk IDs with BM25 ranks
4. Combine scores: vector (0.7) + keyword (0.3)
5. Sort by combined score descending, take top N
6. Return: `{ id, date, summary, score }[]`

The agent gets summaries — lightweight. Full content loaded on demand via `load_memory_chunks`.

---

## Tools

### search_memory

```json
{
  "name": "search_memory",
  "description": "Search past memories by topic. Uses hybrid vector + keyword matching. Returns chunk summaries ranked by relevance.",
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

**Returns:** JSON array of `{ id, date, summary, score }` sorted by relevance descending.

### load_memory_chunks

```json
{
  "name": "load_memory_chunks",
  "description": "Load the full content of specific memory chunks by ID. Use after search_memory to read details.",
  "parameters": {
    "type": "object",
    "properties": {
      "ids": {
        "type": "array",
        "items": { "type": "integer" },
        "description": "Chunk IDs from search_memory results"
      }
    },
    "required": ["ids"]
  }
}
```

**Returns:** JSON array of `{ id, date, content }`.

---

## Configuration

### AgentConfig additions

| Field | Description | Default |
|-------|-------------|---------|
| `embeddingEndpoint` | Azure OpenAI endpoint URL | `""` (disabled) |
| `embeddingApiKey` | API key for embedding calls | `""` |
| `embeddingDeployment` | Deployment name (e.g. `text-embedding-3-small`) | `""` |
| `indexRunAtHour` | Hour (UTC) to run the nightly job | `2` |

Chunking uses `compactionProvider` and `compactionModel`.

**Graceful degradation:** If `embeddingEndpoint` is empty, hosted service warns once, tools not exposed.

---

## Architecture

### New project: `OpenAgent.MemoryIndex`

```
src/agent/OpenAgent.MemoryIndex/
├── OpenAgent.MemoryIndex.csproj
├── MemoryChunkStore.cs           — SQLite persistence, FTS5, cosine similarity
├── EmbeddingClient.cs            — Azure OpenAI Embeddings API client
├── MemoryChunker.cs              — LLM: split file into chunks with summaries
├── MemoryIndexService.cs         — Orchestrates indexing and hybrid search
├── MemoryToolHandler.cs          — IToolHandler: search_memory, load_memory_chunks
├── MemoryIndexHostedService.cs   — IHostedService with daily timer
├── MemoryIndexEndpoints.cs       — REST API for manual trigger and stats
└── ServiceCollectionExtensions.cs
```

### Component relationships

```
MemoryIndexHostedService (timer)
    └── MemoryIndexService (orchestration)
            ├── MemoryChunkStore (SQLite + FTS5)
            ├── MemoryChunker (LLM chunking via Func<string, ILlmTextProvider>)
            └── EmbeddingClient (Azure OpenAI embeddings)

MemoryToolHandler (agent tools)
    └── MemoryIndexService (search, load)

MemoryIndexEndpoints (REST API)
    └── MemoryIndexService (manual trigger, stats)
```

### Dependencies

- `OpenAgent.Contracts` — AgentEnvironment, ILlmTextProvider, ITool, IToolHandler
- `OpenAgent.Models` — Message, CompletionEvent, CompletionOptions, AgentConfig
- `Microsoft.Data.Sqlite` — already in Directory.Packages.props
- `Microsoft.Extensions.Hosting.Abstractions` — already in Directory.Packages.props
- `Microsoft.Extensions.Logging.Abstractions` — already in Directory.Packages.props

No new NuGet packages. FTS5 is built into SQLite.

---

## Approach: In-memory Cosine Similarity

Store embeddings as BLOBs in SQLite. Load all chunks into memory for vector similarity — pure C#, no native dependencies.

Daily logs produce ~3-10 chunks each. A few thousand chunks after years. At 1536 dims x 4 bytes = 6KB per embedding, 5000 chunks = 30MB. Trivially fits in memory. Cached in `MemoryIndexService`, invalidated on index run.

---

## Hosted Service

- **Timer:** Checks every hour via `PeriodicTimer`
- **Guard:** Skips if already ran today
- **Guard:** Skips if current hour (UTC) < `indexRunAtHour`
- **Guard:** Skips if embedding config missing (warns once)
- **Runs:** `MemoryIndexService.RunAsync()`

---

## Endpoints

### POST /api/memory-index/run

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

| Test | What it verifies |
|------|-----------------|
| `MemoryChunkStore_InsertAndQuery` | Insert chunks with summaries, query by date |
| `MemoryChunkStore_DuplicateChunk` | UNIQUE constraint |
| `MemoryChunkStore_EmbeddingRoundtrip` | float[] serialization |
| `MemoryChunkStore_GetProcessedDates` | Distinct dates |
| `MemoryChunkStore_GetAllChunks` | Bulk load with summaries |
| `MemoryChunkStore_FtsSearch` | FTS5 keyword match returns IDs + ranks |
| `MemoryChunkStore_GetChunksByIds` | Load specific chunks by ID |
| `EmbeddingClient_ParsesResponse` | float[] from API JSON |
| `EmbeddingClient_FormatsRequest` | Correct HTTP call |
| `MemoryChunker_ParsesChunksWithSummaries` | Extracts content + summary pairs |
| `MemoryIndexService_ProcessesFiles` | Full flow: chunk → embed → store → delete |
| `MemoryIndexService_SkipsProcessed` | Already-chunked dates skipped |
| `MemoryIndexService_RespectsMemoryWindow` | Files within window stay on disk |
| `MemoryIndexService_HybridSearch` | Vector + keyword combined ranking |
| `MemoryIndexService_LoadChunks` | Load by IDs returns content |
| `CosineSimilarity_KnownVectors` | Math verification |
| `HostedService_Guards` | Skips when unconfigured / already ran |
| `SearchMemoryTool_ReturnsSummaries` | Returns id + summary, not content |
| `LoadMemoryChunksTool_ReturnsContent` | Returns full content by IDs |

---

## Modified Files

| File | Change |
|------|--------|
| `OpenAgent.Models/Configs/AgentConfig.cs` | Add 4 embedding/index config fields |
| `OpenAgent/OpenAgent.csproj` | Add ProjectReference |
| `OpenAgent/Program.cs` | Add `AddMemoryIndex()` + `MapMemoryIndexEndpoints()` |
| `OpenAgent.Tests/OpenAgent.Tests.csproj` | Add ProjectReference |
| `OpenAgent.sln` | Add project |

---

## Not in Scope

- **Digest job** (#19) — separate issue, curates MEMORY.md
- **Background job** (#51) — separate issue, autonomous agent runs
- **Preloading chunks into system prompt** — agent uses tools
- **Re-chunking already processed files** — add reprocess endpoint later if needed
- **Configurable search weights** — hardcoded 0.7/0.3 for now, tune later
- **Settings UI for embedding config** — configure via agent.json
