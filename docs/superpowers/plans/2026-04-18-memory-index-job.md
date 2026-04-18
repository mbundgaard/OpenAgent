# Memory Index Job Implementation Plan (v4 — Embedding Providers + Hybrid Search)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Index daily memory logs into searchable topic chunks with hybrid vector + keyword search. Pluggable embedding providers starting with local ONNX (multilingual-e5-base).

**Architecture:** Three new projects: `IEmbeddingProvider` interface in Contracts, `OnnxEmbeddingProvider` in Embedding.Onnx, and MemoryIndex for chunking/search/storage. LLM chunks files into topics with summaries. Hybrid search: 0.7 cosine + 0.3 FTS5 BM25. Two tools: `search_memory` (returns summaries), `load_memory_chunks` (returns full content).

**Tech Stack:** .NET 10, Microsoft.ML.OnnxRuntime, Microsoft.ML.Tokenizers, Microsoft.Data.Sqlite, FTS5, xUnit

**Spec:** [docs/superpowers/specs/2026-04-16-memory-index-job-design.md](../specs/2026-04-16-memory-index-job-design.md)

**Supersedes:** All previous plans (v1-v3)

---

## Reference: Key patterns to follow

- **Provider pattern:** See `ILlmTextProvider` — keyed singletons, resolved via `Func<string, T>`, selected by `AgentConfig` field
- **Tool pattern:** See `FileReadTool` / `FileSystemToolHandler` — `ITool` with `AgentToolDefinition`, `IToolHandler` with conditional tool list
- **SQLite pattern:** See `SqliteConversationStore` — `Open()` method, WAL pragma in `InitializeDatabase()` only
- **LLM raw call pattern:** See `CompactionSummarizer` — `Func<string, ILlmTextProvider>`, `CompletionOptions { ResponseFormat = "json_object" }`, collect `TextDelta` events
- **Test pattern:** Temp directories with `Guid.NewGuid()`, `IDisposable` cleanup, `StreamingTextProvider` for fake LLM responses
- **DI pattern:** See `ScheduledTasks/ServiceCollectionExtensions.cs`

---

## File Map

### New files

| File | Responsibility |
|------|---------------|
| `OpenAgent.Contracts/IEmbeddingProvider.cs` | Interface + `EmbeddingPurpose` enum |
| `OpenAgent.Embedding.Onnx/OpenAgent.Embedding.Onnx.csproj` | Project file (OnnxRuntime, ML.Tokenizers, ref Contracts) |
| `OpenAgent.Embedding.Onnx/OnnxEmbeddingProvider.cs` | multilingual-e5-base ONNX pipeline |
| `OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj` | Project file (Sqlite, Hosting, ref Contracts+Models, FrameworkReference AspNetCore) |
| `OpenAgent.MemoryIndex/MemoryChunkStore.cs` | SQLite + FTS5 persistence, cosine similarity |
| `OpenAgent.MemoryIndex/MemoryChunker.cs` | LLM chunking with summaries |
| `OpenAgent.MemoryIndex/MemoryIndexService.cs` | Indexing + hybrid search orchestration |
| `OpenAgent.MemoryIndex/MemoryToolHandler.cs` | `search_memory` + `load_memory_chunks` tools |
| `OpenAgent.MemoryIndex/MemoryIndexHostedService.cs` | Daily timer |
| `OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs` | REST API |
| `OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs` | DI registration |
| `OpenAgent.Tests/Fakes/FakeEmbeddingProvider.cs` | Test double for `IEmbeddingProvider` |

### Modified files

| File | Change |
|------|--------|
| `OpenAgent.Contracts/` | Add `IEmbeddingProvider.cs` |
| `OpenAgent.Models/Configs/AgentConfig.cs` | Add `embeddingProvider`, `indexRunAtHour` |
| `OpenAgent/OpenAgent.csproj` | Add ProjectReferences to MemoryIndex + Embedding.Onnx |
| `OpenAgent/Program.cs` | Register providers, add services + endpoints |
| `OpenAgent.Tests/OpenAgent.Tests.csproj` | Add ProjectReferences |
| `OpenAgent.sln` | Add 2 projects |
| `Directory.Packages.props` | Add `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers` |

---

## Task 1: Project Scaffolding

- [ ] Create `IEmbeddingProvider` interface in `OpenAgent.Contracts/IEmbeddingProvider.cs`:
  - `EmbeddingPurpose` enum: `Indexing`, `Search`
  - Interface: `Key` (string), `Dimensions` (int), `GenerateEmbeddingAsync(string text, EmbeddingPurpose purpose, CancellationToken ct)`
- [ ] Create `OpenAgent.Embedding.Onnx` project — references Contracts, packages: OnnxRuntime, ML.Tokenizers. Add `InternalsVisibleTo` for Tests.
- [ ] Create `OpenAgent.MemoryIndex` project — references Contracts + Models, packages: Sqlite, Hosting.Abstractions, Logging.Abstractions. `FrameworkReference` AspNetCore. Add `InternalsVisibleTo`.
- [ ] Add both projects to solution, add ProjectReferences from host + test projects
- [ ] Add NuGet package versions to `Directory.Packages.props`
- [ ] Add `AgentConfig` fields: `embeddingProvider` (string, default `""`), `indexRunAtHour` (int, default `2`)
- [ ] Build to verify
- [ ] Commit

---

## Task 2: MemoryChunkStore — SQLite + FTS5

**Records:**
- `ChunkEntry` — input: `Content`, `Summary`, `Embedding` (float[])
- `StoredChunk` — output: `Id`, `Date`, `ChunkIndex`, `Content`, `Summary`, `Embedding`
- `ChunkStats` — `TotalChunks`, `TotalDays`, `OldestDate`, `NewestDate` (with `[JsonPropertyName]`)

**Methods:**
- `InsertChunks(string date, IReadOnlyList<ChunkEntry>)` — inserts into both `memory_chunks` AND `memory_chunks_fts` in one transaction. Use `last_insert_rowid()` to get the ID for the FTS insert.
- `GetProcessedDates()` → `HashSet<string>` — `SELECT DISTINCT date`
- `GetAllChunks()` → `List<StoredChunk>` — ordered by date, chunk_index
- `GetChunksByIds(IReadOnlyList<int>)` → `List<StoredChunk>` — dynamic parameterized IN clause, no embedding loaded
- `SearchFts(string query)` → `Dictionary<int, float>` — `SELECT rowid, rank FROM memory_chunks_fts WHERE ... MATCH @query`. Normalize rank: `1 / (1 + abs(rank))`
- `GetStats()` → `ChunkStats`
- Static: `CosineSimilarity`, `SerializeEmbedding`, `DeserializeEmbedding`

**Constructor:** Takes `AgentEnvironment` (public) or `string dbPath` (internal, for tests). DB at `{dataPath}/memory.db`. WAL pragma in `InitializeDatabase()` only. Creates both `memory_chunks` table and `memory_chunks_fts` virtual table.

**Tests** (`MemoryChunkStoreTests`):
- [ ] Insert chunks with summaries, verify `GetProcessedDates` returns the date
- [ ] Duplicate date+chunk_index throws `SqliteException`
- [ ] Embedding serialization roundtrip preserves exact values
- [ ] `GetAllChunks` returns content, summary, and embeddings
- [ ] `GetChunksByIds` returns specific chunks
- [ ] `SearchFts` finds matching content, returns normalized scores > 0
- [ ] `SearchFts` with no matches returns empty dictionary
- [ ] `GetStats` empty and with entries
- [ ] `CosineSimilarity` — identical vectors = 1, orthogonal = 0, zero vector = 0
- [ ] Commit

---

## Task 3: OnnxEmbeddingProvider

**Implementation** (`OnnxEmbeddingProvider.cs`):
- Implements `IEmbeddingProvider`, `IDisposable`
- Key: `"onnx"`, Dimensions: `768`
- Constructor loads ONNX model from `{dataPath}/models/multilingual-e5-base/model.onnx` and tokenizer from `tokenizer.json` in same directory
- `GenerateEmbeddingAsync`: prepends `"query: "` (Search) or `"passage: "` (Indexing), tokenizes, runs ONNX inference, mean pools over sequence length, L2 normalizes
- Internal static methods `MeanPool` and `L2Normalize` for testability

**Note:** The `Microsoft.ML.Tokenizers` API for loading `tokenizer.json` may vary by version. Check NuGet docs at implementation time. The model files must be downloaded from HuggingFace and placed in the model directory before running.

**Tests** (`OnnxEmbeddingProviderTests`):
- [ ] `MeanPool` with known input produces expected output
- [ ] `L2Normalize` produces unit-length vector
- [ ] `L2Normalize` handles zero vector without NaN
- [ ] Commit

(Full ONNX inference tests require model files — add integration test that skips when model not present.)

---

## Task 4: MemoryChunker — LLM Topic Splitting

**Implementation** (`MemoryChunker.cs`):
- Takes `Func<string, ILlmTextProvider>` and `AgentConfig`
- `ChunkFileAsync(string fileContent)` — single LLM call with system prompt from spec, `json_object` response format, returns `IReadOnlyList<ChunkResult>`
- `ChunkResult` record: `Content`, `Summary`
- Internal static `ParseChunksResponse(string json)` — extracts array of `{ content, summary }` objects

**System prompt:** From the spec — restructure into topic chunks, each with content + summary. Output JSON `{ "chunks": [{ "content": "...", "summary": "..." }] }`.

**Tests** (`MemoryChunkerTests`):
- [ ] `ParseChunksResponse` extracts content + summary pairs
- [ ] `ParseChunksResponse` handles empty array
- [ ] `ChunkFileAsync` calls LLM and returns parsed chunks (use `StreamingTextProvider` with canned JSON)
- [ ] Commit

---

## Task 5: MemoryIndexService — Indexing + Hybrid Search

**Implementation** (`MemoryIndexService.cs`):

Records:
- `IndexResult` — `FilesScanned`, `FilesProcessed`, `ChunksCreated`, `Errors` (with `[JsonPropertyName]`)
- `SearchResult` — `Id`, `Date`, `Summary`, `Score` (with `[JsonPropertyName]`)
- `LoadResult` — `Id`, `Date`, `Content` (with `[JsonPropertyName]`)

Constructor takes: `MemoryChunkStore`, `MemoryChunker`, `Func<string, IEmbeddingProvider>`, `AgentConfig`, `AgentEnvironment`, `ILogger`

Methods:
- `RunAsync(ct)` — scan `memory/*.md` files past `memoryDays` window, filter by processed dates, for each: read → chunk via LLM → embed each chunk (with `EmbeddingPurpose.Indexing`) → store → delete file. Invalidate cache. Return `IndexResult`.
- `SearchAsync(query, limit, ct)` — embed query (with `EmbeddingPurpose.Search`) → cosine similarity against cached chunks → FTS5 keyword search → combine: `0.7 * cosine + 0.3 * ftsNormalized` → sort → top N. Return `List<SearchResult>`.
- `LoadChunksAsync(ids)` → `List<LoadResult>` — delegates to `store.GetChunksByIds`
- `GetStats()` → delegates to store
- Private `_cachedChunks` field, invalidated on `RunAsync`

**Tests** (`MemoryIndexServiceTests`) — use `FakeEmbeddingProvider` and `StreamingTextProvider`:
- [ ] `RunAsync` processes files past the memory window, creates chunks, deletes source file
- [ ] `RunAsync` skips files within the memory window (file stays on disk)
- [ ] `RunAsync` skips dates that already have chunks in DB
- [ ] `RunAsync` skips files shorter than 50 chars
- [ ] `SearchAsync` returns ranked results — insert chunks with known embeddings, search with matching fake embedding, verify ranking
- [ ] `SearchAsync` hybrid scoring — insert chunks, put a keyword match on a lower-vector-score chunk, verify keyword boost changes ranking
- [ ] `LoadChunksAsync` returns content for specific IDs
- [ ] Empty memory directory returns zero counts
- [ ] Commit

---

## Task 6: MemoryToolHandler — Two Tools

**Implementation** (`MemoryToolHandler.cs`):
- `MemoryToolHandler : IToolHandler` — takes `MemoryIndexService` and `AgentConfig`. Exposes tools only when `embeddingProvider` is configured.
- `SearchMemoryTool : ITool` — name `"search_memory"`, params: `query` (required), `limit` (optional, default 5). Calls `service.SearchAsync`. Returns `{ results: [{ id, date, summary, score }] }`.
- `LoadMemoryChunksTool : ITool` — name `"load_memory_chunks"`, params: `ids` (required, int array). Calls `service.LoadChunksAsync`. Returns `{ chunks: [{ id, date, content }] }`.

**Tests** (`MemoryToolHandlerTests`) — use `FakeEmbeddingProvider`:
- [ ] Exposes two tools when embedding provider configured
- [ ] Exposes no tools when embedding provider not configured
- [ ] `search_memory` returns summaries with correct shape (id, date, summary, score)
- [ ] `search_memory` respects limit parameter
- [ ] `load_memory_chunks` returns full content for given IDs
- [ ] Commit

---

## Task 7: Hosted Service + Guard Tests

**Implementation** (`MemoryIndexHostedService.cs`):
- `IHostedService, IDisposable`
- Hourly `PeriodicTimer`
- Guards: not configured → warn once + skip. Already ran today → skip. Before `indexRunAtHour` → skip.
- Delegates to `MemoryIndexService.RunAsync`
- `CheckAndRunAsync` is `internal` for testing

**Tests** (`MemoryIndexHostedServiceTests`):
- [ ] Skips when `embeddingProvider` is empty (no exception)
- [ ] Runs once per day (second call is no-op)
- [ ] Commit

---

## Task 8: Endpoints + DI Wiring

**Endpoints** (`MemoryIndexEndpoints.cs`):
- `POST /api/memory-index/run` → `service.RunAsync()` → `Results.Ok(indexResult)`
- `GET /api/memory-index/stats` → `service.GetStats()` → `Results.Ok(stats)`
- Both require authorization

**DI** (`ServiceCollectionExtensions.cs`):
- `AddMemoryIndex()` — registers `MemoryChunkStore`, `MemoryChunker`, `MemoryIndexService`, `IToolHandler` → `MemoryToolHandler`, `HostedService` → `MemoryIndexHostedService`

**Program.cs:**
- Register `OnnxEmbeddingProvider` as keyed `IEmbeddingProvider` with key `"onnx"`
- Register `Func<string, IEmbeddingProvider>` factory
- Call `AddMemoryIndex()` and `MapMemoryIndexEndpoints()`

- [ ] Implement endpoints
- [ ] Implement ServiceCollectionExtensions
- [ ] Wire into Program.cs
- [ ] Build full solution
- [ ] Run all tests
- [ ] Commit

---

## Spec Coverage

| Requirement | Task |
|---|---|
| IEmbeddingProvider interface (like ILlmTextProvider) | 1 |
| ONNX provider with multilingual-e5-base | 3 |
| e5 prefix: "query: " / "passage: " | 3 (provider), 5 (service calls with purpose) |
| memory_chunks table with content + summary | 2 |
| FTS5 virtual table + sync | 2 |
| Hybrid search: 0.7 cosine + 0.3 BM25 | 5 |
| LLM chunking with summaries | 4 |
| search_memory returns { id, date, summary, score } | 6 |
| load_memory_chunks returns content by IDs | 6 |
| File deleted after processing | 5 |
| Processing threshold = memoryDays | 5 |
| memory.db database | 2 |
| Cached chunks, invalidated on RunAsync | 5 |
| No preloading from DB | n/a (not built) |
| Graceful degradation (tools hidden when unconfigured) | 6 |
| Hosted service guards | 7 |
| REST endpoints | 8 |
| DI + Program.cs wiring | 8 |
| AgentConfig: embeddingProvider, indexRunAtHour | 1 |
| EmbeddingPurpose enum (Indexing/Search) | 1 |
| Keyed singleton + Func factory | 8 |
