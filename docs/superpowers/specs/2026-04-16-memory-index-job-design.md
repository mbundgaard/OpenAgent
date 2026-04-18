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
- **Past the window:** Nightly job processes the file into chunks in `memory.db`, then deletes the file.
- **No overlap:** A day's content is either on disk (recent) or in the DB (processed). Never both.
- **Nothing from `memory.db` is preloaded into the system prompt.** The agent uses tools.

---

## Embedding Provider Architecture

Follows the same pattern as `ILlmTextProvider` — pluggable providers, keyed singletons, resolved via `AgentConfig.EmbeddingProvider`.

### Interface (in `OpenAgent.Contracts`)

```csharp
public interface IEmbeddingProvider
{
    string Key { get; }
    int Dimensions { get; }
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}
```

### First implementation: ONNX (`OpenAgent.Embedding.Onnx`)

- **Model:** `multilingual-e5-base` — 768 dims, multilingual (Danish + English), WordPiece-compatible via HuggingFace `tokenizer.json`
- **Runtime:** `Microsoft.ML.OnnxRuntime` for inference, `Microsoft.ML.Tokenizers` for tokenization
- **Model files:** `{dataPath}/models/multilingual-e5-base/` — contains `model.onnx`, `tokenizer.json`, `tokenizer_config.json`
- **e5 prefix convention:** `"query: {text}"` for search queries, `"passage: {text}"` for chunk content during indexing
- **Pipeline:** Tokenize → ONNX inference → mean pooling → L2 normalize → `float[768]`
- **RAM:** ~1-1.5 GB for the model

### Resolution

```csharp
// AgentConfig
[JsonPropertyName("embeddingProvider")]
public string EmbeddingProvider { get; set; } = "";

// DI registration
builder.Services.AddKeyedSingleton<IEmbeddingProvider, OnnxEmbeddingProvider>("onnx");
builder.Services.AddSingleton<Func<string, IEmbeddingProvider>>(sp =>
    key => sp.GetRequiredKeyedService<IEmbeddingProvider>(key));
```

Future: add `AzureOpenAiEmbeddingProvider` as another keyed singleton. Same interface, different key.

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
| `embedding` | BLOB | `float[]` as little-endian bytes (dimension depends on provider) |

FTS5 table kept in sync by inserting into both tables in the same transaction.

---

## Indexing Flow

1. **Scan** — find `.md` files past the `memoryDays` window
2. **Filter** — skip dates that already have chunks in DB
3. **For each unprocessed file** (oldest first):
   a. Read file content, skip if < 50 chars
   b. **LLM call:** Chunk into topics, each with content + summary
   c. **Embedding calls:** Generate embedding for each chunk's content (prefixed with `"passage: "`)
   d. Insert all chunks into `memory_chunks` + `memory_chunks_fts`
   e. **Delete the source file**
4. **On error** — log, skip, file stays on disk for retry

### LLM chunking

Single call per file. Uses `compactionProvider` / `compactionModel`.

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

---

## Search Flow — Hybrid (Vector + Keyword)

### 1. Vector search

Cosine similarity between query embedding (prefixed `"query: "`) and all chunk embeddings. Captures semantic meaning.

### 2. Keyword search

FTS5 MATCH on summary + content. Captures exact terms — names, IDs, URLs.

### 3. Combined scoring

```
final_score = (0.7 * cosine_score) + (0.3 * normalized_fts_score)
```

FTS5 rank normalized: `1 / (1 + abs(rank))`. Chunks not in FTS results get 0.

### Flow

1. `search_memory(query)` → embed query (with `"query: "` prefix) → cosine similarity
2. FTS5 MATCH query → matching IDs with BM25 ranks
3. Combine scores, sort, take top N
4. Return `{ id, date, summary, score }[]`

---

## Tools

### search_memory

**Returns:** `{ id, date, summary, score }[]` — lightweight summaries for scanning.

### load_memory_chunks

**Input:** `{ ids: [1, 5, 12] }`
**Returns:** `{ id, date, content }[]` — full chunk content for selected IDs.

---

## Architecture

### Projects

```
OpenAgent.Contracts/
    IEmbeddingProvider.cs             — interface

OpenAgent.Embedding.Onnx/
    OnnxEmbeddingProvider.cs          — multilingual-e5-base via ONNX Runtime
    OnnxEmbeddingProvider.csproj

OpenAgent.MemoryIndex/
    MemoryChunkStore.cs               — SQLite + FTS5
    MemoryChunker.cs                  — LLM chunking with summaries
    MemoryIndexService.cs             — indexing + hybrid search orchestration
    MemoryToolHandler.cs              — search_memory + load_memory_chunks
    MemoryIndexHostedService.cs       — daily timer
    MemoryIndexEndpoints.cs           — REST API
    ServiceCollectionExtensions.cs
```

### Dependencies

| Package | Project | Already in CPM? |
|---------|---------|-----------------|
| `Microsoft.ML.OnnxRuntime` | Embedding.Onnx | No — add |
| `Microsoft.ML.Tokenizers` | Embedding.Onnx | No — add |
| `Microsoft.Data.Sqlite` | MemoryIndex | Yes |
| `Microsoft.Extensions.Hosting.Abstractions` | MemoryIndex | Yes |

---

## Configuration

### AgentConfig additions

| Field | Description | Default |
|-------|-------------|---------|
| `embeddingProvider` | Embedding provider key (e.g. `"onnx"`) | `""` (disabled) |
| `indexRunAtHour` | Hour (UTC) to run the nightly job | `2` |

Chunking uses `compactionProvider` and `compactionModel`.

**Graceful degradation:** If `embeddingProvider` is empty, hosted service warns once, tools not exposed.

---

## Model Files

The ONNX provider expects model files at `{dataPath}/models/multilingual-e5-base/`:
- `model.onnx` — the ONNX model
- `tokenizer.json` — HuggingFace tokenizer definition
- `tokenizer_config.json` — tokenizer config

These are downloaded from HuggingFace and bundled in the Docker image. Not embedded in the assembly — too large (~1 GB).

---

## Hosted Service

- Checks every hour via `PeriodicTimer`
- Guards: already ran today, before configured hour, provider not configured
- Delegates to `MemoryIndexService.RunAsync()`

---

## Endpoints

- `POST /api/memory-index/run` — manual trigger
- `GET /api/memory-index/stats` — counts and date range

Both require authorization.

---

## Testing

| Test | What it verifies |
|------|-----------------|
| `MemoryChunkStore_InsertAndQuery` | Insert with summaries, query by date |
| `MemoryChunkStore_DuplicateChunk` | UNIQUE constraint |
| `MemoryChunkStore_EmbeddingRoundtrip` | float[] serialization |
| `MemoryChunkStore_GetProcessedDates` | Distinct dates |
| `MemoryChunkStore_GetAllChunks` | Bulk load |
| `MemoryChunkStore_FtsSearch` | Keyword match returns IDs + ranks |
| `MemoryChunkStore_GetChunksByIds` | Load specific chunks |
| `MemoryChunker_ParsesChunksWithSummaries` | Content + summary extraction |
| `MemoryIndexService_ProcessesFiles` | Full flow including file deletion |
| `MemoryIndexService_RespectsMemoryWindow` | Files within window untouched |
| `MemoryIndexService_HybridSearch` | Vector + keyword combined ranking |
| `MemoryIndexService_LoadChunks` | Load by IDs |
| `CosineSimilarity_KnownVectors` | Math verification |
| `HostedService_Guards` | Skip when unconfigured / already ran |
| `SearchMemoryTool_ReturnsSummaries` | Returns id + summary |
| `LoadMemoryChunksTool_ReturnsContent` | Returns full content by IDs |
| `OnnxEmbeddingProvider_GeneratesEmbedding` | Returns float[768] for input text |
| `OnnxEmbeddingProvider_QueryPrefix` | Prepends "query: " for search |
| `OnnxEmbeddingProvider_PassagePrefix` | Prepends "passage: " for indexing |

---

## Modified Files

| File | Change |
|------|--------|
| `OpenAgent.Contracts/` | Add `IEmbeddingProvider.cs` |
| `OpenAgent.Models/Configs/AgentConfig.cs` | Add `embeddingProvider`, `indexRunAtHour` |
| `OpenAgent/OpenAgent.csproj` | Add references to MemoryIndex + Embedding.Onnx |
| `OpenAgent/Program.cs` | Register providers, add services + endpoints |
| `OpenAgent.Tests/OpenAgent.Tests.csproj` | Add references |
| `OpenAgent.sln` | Add 2 new projects |
| `Directory.Packages.props` | Add OnnxRuntime + ML.Tokenizers versions |

---

## Not in Scope

- **Azure OpenAI embedding provider** — future provider, same interface
- **Digest job** (#19) / **Background job** (#51)
- **Preloading chunks into system prompt**
- **Configurable search weights** — hardcoded 0.7/0.3
- **Model auto-download** — model files placed manually or in Docker build
