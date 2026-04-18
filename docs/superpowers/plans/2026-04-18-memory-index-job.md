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
- [ ] Add `AgentConfig` fields: `embeddingProvider` (string, default `""`), `indexRunAtHour` (int, default `2`, interpreted as Europe/Copenhagen to match the rest of the agent)
- [ ] Build to verify
- [ ] Commit

---

## Task 2: MemoryChunkStore — SQLite + FTS5

**Schema:** `memory_chunks` includes `provider TEXT NOT NULL` and `dimensions INTEGER NOT NULL` columns so chunks from different embedding providers can coexist. Vector search filters by the current provider; FTS5 is provider-agnostic.

**Records:**
- `ChunkEntry` — input: `Content`, `Summary`, `Embedding` (float[])
- `StoredChunk` — output: `Id`, `Date`, `ChunkIndex`, `Content`, `Summary`, `Embedding`, `Provider`, `Dimensions`
- `ChunkStats` — `TotalChunks`, `TotalDays`, `OldestDate`, `NewestDate` (with `[JsonPropertyName]`)

**Methods:**
- `InsertChunks(string date, string provider, int dimensions, IReadOnlyList<ChunkEntry>)` — inserts into both `memory_chunks` AND `memory_chunks_fts` in one transaction. Use `last_insert_rowid()` to get the ID for the FTS insert.
- `GetProcessedDates()` → `HashSet<string>` — `SELECT DISTINCT date`
- `GetAllChunks(string provider)` → `List<StoredChunk>` — filtered to the given provider, ordered by date, chunk_index. Vector search only ranks chunks embedded by the current provider.
- `GetChunksByIds(IReadOnlyList<int>)` → `List<StoredChunk>` — dynamic parameterized IN clause, no embedding loaded. No provider filter — `load_memory_chunks` returns raw content regardless of embedding provenance.
- `SearchFts(string query)` → `Dictionary<int, float>` — `SELECT rowid, rank FROM memory_chunks_fts WHERE ... MATCH @query`. Normalize rank: `abs(rank) / (1 + abs(rank))` — maps 0→0, -1→0.5, -10→~0.91. Larger = better. (FTS5 rank is non-positive; the naive `1/(1+abs(rank))` inverts the ordering.)
- `GetStats()` → `ChunkStats`
- Static: `CosineSimilarity`, `SerializeEmbedding`, `DeserializeEmbedding`

**Constructor:** Takes `AgentEnvironment` (public) or `string dbPath` (internal, for tests). DB at `{dataPath}/memory.db`. WAL pragma in `InitializeDatabase()` only. Creates both `memory_chunks` table and `memory_chunks_fts` virtual table.

**Tests** (`MemoryChunkStoreTests`):
- [ ] Insert chunks with summaries, verify `GetProcessedDates` returns the date
- [ ] Duplicate date+chunk_index throws `SqliteException`
- [ ] Embedding serialization roundtrip preserves exact values
- [ ] `GetAllChunks(provider)` returns only rows for that provider with content, summary, embeddings, dimensions
- [ ] `GetAllChunks("other")` returns empty when all rows belong to a different provider
- [ ] `GetChunksByIds` returns specific chunks (all providers)
- [ ] `SearchFts` finds matching content — higher-rank matches produce higher normalized scores
- [ ] `SearchFts` with no matches returns empty dictionary
- [ ] `GetStats` empty and with entries
- [ ] `CosineSimilarity` — identical vectors = 1, orthogonal = 0, zero vector = 0
- [ ] Commit

---

## Task 3: OnnxEmbeddingProvider

**Pre-work spike (do first, commit alone):** multilingual-e5-base uses an XLM-RoBERTa SentencePiece/BPE tokenizer, not WordPiece. Before writing the provider, verify that `Microsoft.ML.Tokenizers` can load the model's `tokenizer.json` and produces the same token IDs as HuggingFace for a known input. If the library can't load it, fall back to shelling out or vendoring a BPE implementation — but *know* that before Task 3 architecture lands. Drop a ~20-line console scratch test and delete it after, or keep it as a disabled integration test.

**Implementation** (`OnnxEmbeddingProvider.cs`):
- Implements `IEmbeddingProvider`, `IDisposable`
- Key: `"onnx"`, Dimensions: `768`
- Constructor loads ONNX model from `{dataPath}/models/multilingual-e5-base/model.onnx` and tokenizer from `tokenizer.json` in same directory
- `GenerateEmbeddingAsync(text, purpose, ct)`: prepends `"query: "` (Search) or `"passage: "` (Indexing), tokenizes, **truncates to 512 tokens** (model max sequence length), runs ONNX inference, mean-pools over non-padding tokens, L2 normalizes
- Internal static methods `MeanPool` and `L2Normalize` for testability

**Model files:** Downloaded from HuggingFace and placed at `{dataPath}/models/multilingual-e5-base/` before running (or bundled into the Docker image). The provider must throw a clear error on construction if the files are missing rather than at first-use.

**Tests** (`OnnxEmbeddingProviderTests`):
- [ ] `MeanPool` with known input + attention mask produces expected output (ignores padding positions)
- [ ] `L2Normalize` produces unit-length vector
- [ ] `L2Normalize` handles zero vector without NaN
- [ ] Truncation: input longer than 512 tokens is truncated, not rejected
- [ ] Integration test guarded by model-file presence — skip when `model.onnx` not on disk; when present, verify `float[768]` output and that `query:`/`passage:` prefixes produce different embeddings for the same text
- [ ] Commit

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
- `RunAsync(ct)` — resolve current embedding provider from `AgentConfig.EmbeddingProvider`. Scan `memory/*.md` files past `memoryDays` window, filter by processed dates, for each: read → chunk via LLM → embed each chunk (with `EmbeddingPurpose.Indexing`) → store with `provider.Key` and `provider.Dimensions` → delete file. Invalidate cache. Return `IndexResult`.
- `SearchAsync(query, limit, ct)` — resolve current provider. Embed query (with `EmbeddingPurpose.Search`) → cosine similarity against cached chunks (`store.GetAllChunks(provider.Key)`) → FTS5 keyword search (provider-agnostic, but rows from other providers score 0 on the vector side so they only win on strong keyword hits) → combine: `0.7 * cosine + 0.3 * ftsNormalized` → sort → top N. Return `List<SearchResult>`.
- `LoadChunksAsync(ids)` → `List<LoadResult>` — delegates to `store.GetChunksByIds`
- `GetStats()` → delegates to store
- Private `_cachedChunks` keyed by provider, invalidated on `RunAsync` and when the configured provider changes

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
- Guards, in order: `embeddingProvider` empty → warn once + skip. Current Europe/Copenhagen hour < `indexRunAtHour` → skip. Today's date already present in `store.GetProcessedDates()` → skip (so restarts don't cause re-runs).
- Uses Europe/Copenhagen for "today" to match the rest of the agent.
- Delegates to `MemoryIndexService.RunAsync`
- `CheckAndRunAsync` is `internal` for testing, takes an injectable clock/time source

**Tests** (`MemoryIndexHostedServiceTests`):
- [ ] Skips when `embeddingProvider` is empty (no exception)
- [ ] Skips when local hour < `indexRunAtHour`
- [ ] Skips when today is already in `GetProcessedDates()` — proves the "already ran" check is DB-derived, not in-memory
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
