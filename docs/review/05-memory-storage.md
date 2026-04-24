# Memory & Storage Review — 2026-04-23

## TL;DR

The SQLite stores adopt the "new connection per call" discipline (`Microsoft.Data.Sqlite` is thread-safe at the pool level when you do this), and that choice mostly works — but the stores still carry several real integrity gaps. The top finding is the **FTS5 external-content mirror drift** the moment any row is deleted or updated (no contentless triggers, no manual FTS sync) — dormant today because nothing deletes chunks, latent for tomorrow. Close behind: the **non-atomic "chunks committed / source file moved" pair** that produces duplicate content in prompt + index after a crash; **`TryAddColumn` silently swallowing every `SqliteException`** (masking real schema errors including the several `NOT NULL DEFAULT`-column ALTERs that may fail on existing data); **unvalidated FTS5 MATCH queries** that throw `SqliteException` on ordinary English input with quotes/colons/dashes; and **`FileConfigStore.Save` has no lock and no tmp-rename** so a crash or race corrupts `agent.json`. Minor concerns around compaction lock rehydration on restart, `ActiveSkills` JSON deserialize throwing on corruption, and the `Update()` conversation path silently dropping `DisplayName`.

## Strengths

- `MemoryChunkStore.InsertChunks` is transactional — the main-table + FTS mirror INSERTs commit together so a mid-insert crash leaves nothing partially indexed (`MemoryChunkStore.cs:97-142`).
- `MemoryIndexService` has an explicit `SemaphoreSlim _runLock` around `RunAsync` to serialize startup tick vs manual `/run` invocation, and the test `RunAsync_serializes_concurrent_calls_no_duplicate_work` asserts this directly (`MemoryIndexService.cs:56-90`, `MemoryIndexServiceTests.cs:229-253`).
- `CosineSimilarity` and `L2Normalize` both guard against zero vectors and mismatched lengths explicitly, returning 0 rather than NaN (`MemoryChunkStore.cs:302-316`, E5/BGE `L2Normalize`).
- Embedding BLOB storage uses `BinaryPrimitives.WriteSingleLittleEndian` / `ReadSingleLittleEndian` — endianness is pinned, not platform-dependent, and the roundtrip is unit-tested (`MemoryChunkStoreTests.cs:54-64`).
- Provider+model are stored per chunk and `GetAllChunks` filters by `(provider, model)`, so cosine math never crosses incompatible vector spaces (`MemoryChunkStore.cs:164-185`).
- Download-to-tmp + `File.Move` atomic rename on both embedding providers; `.tmp` is best-effort deleted on failure (`OnnxMultilingualE5EmbeddingProvider.cs:157-178`, `OnnxBgeEmbeddingProvider.cs:161-182`).
- `EnsureLoadedAsync` uses a double-checked `SemaphoreSlim` around model load — multi-threaded first use serializes correctly.
- Chunker parsing tolerates Anthropic's ```json-fenced output by extracting the `{...}` span; tests cover fenced, empty-string, missing-keys, and partial-entry cases (`MemoryChunker.cs:94-125`, `MemoryChunkerTests.cs`).
- Parameterized IN-clauses in `GetChunksByIds` and `GetMessagesByIds` — no string concatenation of user ids into SQL (`MemoryChunkStore.cs:191-220`, `SqliteConversationStore.cs:372-393`).
- `FileConnectionStore` uses `SemaphoreSlim` around every read/write, preventing concurrent-writer torn writes (contrast with `FileConfigStore` below).
- `EmbeddingPurpose` enum is plumbed all the way through; the e5 test verifies query vs passage embeddings differ as required by the model.

## Bugs

### FTS5 external-content index drifts on delete/update — no triggers, no mirror cleanup (severity: high)

- **Location:** `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs:67-90` (schema); no DELETE/UPDATE paths anywhere
- **Issue:** `memory_chunks_fts` is declared `content=memory_chunks, content_rowid=id`. That's the *external-content* FTS5 mode, which tells SQLite the FTS table mirrors an external source. SQLite's FTS5 docs explicitly call this out: "it is the responsibility of the user to keep the FTS5 index and content in sync." No `AFTER DELETE` / `AFTER UPDATE` triggers exist. As long as rows are only ever inserted this is dormant — but the moment a rebuild tool, schema migration, or future "forget memory" feature deletes a row, the FTS mirror keeps pointing at rowids that no longer exist; subsequent INSERTs can reuse the freed rowids and the FTS mirror will conflate old and new content.
- **Risk:** Silent index corruption on any future deletion; `SearchFts` returns ghost or mismatched hits; zero external signal.
- **Fix:** Add `CREATE TRIGGER IF NOT EXISTS memory_chunks_ad AFTER DELETE ON memory_chunks BEGIN INSERT INTO memory_chunks_fts(memory_chunks_fts, rowid, summary, content) VALUES('delete', old.id, old.summary, old.content); END;` and the `AFTER UPDATE` twin. Alternative: switch to a non-external-content FTS table and pay duplicated storage.

### Chunks committed but source-file move fails — prompt + index both contain the content (severity: high)

- **Location:** `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs:161-165`; `MoveToBackup` at `184-190`
- **Issue:** Sequence is `_store.InsertChunks(...)` (atomic DB commit) → `MoveToBackup(...)` (OS rename). If the process is killed, the disk fills, an AV scanner holds a handle, or the rename is cross-volume, chunks land in the DB but the source file stays in `memory/` root. Next run: `alreadyProcessed.Contains(date)` is true → `continue`, so no duplicate insert — **but** `SystemPromptBuilder` still loads the file from `memory/` root into the system prompt. Same-day content now lives in both the prompt AND the index. Worse, if a rebuild tool ever ignores `alreadyProcessed` the second run hits `UNIQUE(date, chunk_index)` and fails all chunks for that date.
- **Risk:** Content duplication in prompt + index; no observability (no warning that a known-indexed date still has a source file in `memory/`).
- **Fix:** At the top of `RunInternalAsync`, for every date in `alreadyProcessed` whose source file is still in `memory/` root, move it to `backup/` (self-healing). Cheap and retroactive. Or: move the source file to `memory/backup/staging/` *before* inserting chunks, then atomic-rename staging → backup after the commit.

### `TryAddColumn` swallows every `SqliteException` — real schema errors silently ignored (severity: high)

- **Location:** `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:524-536`
- **Issue:** The catch is unconditional on `SqliteException`. "Duplicate column name" is one error class; others — "no such table", "database is locked", constraint violations on `NOT NULL DEFAULT` added to populated tables, disk I/O — are silently swallowed too. The code assumes every exception is benign. Broken migrations produce a store that looks healthy but is missing columns; the next INSERT/SELECT throws `no such column` deep in request handling and the root cause is invisible.
- **Risk:** Silent schema drift; operators discover missing columns only via downstream feature failures.
- **Fix:** Inspect `ex.SqliteErrorCode` / `ex.Message` for the specific duplicate-column case (`ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)`); rethrow everything else. Fail loud at startup.

### ALTER TABLE adding `NOT NULL DEFAULT` on populated tables can fail — silently via the above (severity: high)

- **Location:** `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:86-90, 99`
- **Issue:** Several migrations use `"TEXT NOT NULL DEFAULT ''"` / `"INTEGER NOT NULL DEFAULT 0"`. SQLite's constraint for `ALTER TABLE ADD COLUMN NOT NULL` requires a non-NULL constant default; empty string / 0 qualify on modern SQLite, but older engines or specific row-patterns (e.g. the FK cascade on `Messages` referencing a missing row) can reject the ADD. Because `TryAddColumn` catches everything, a failed ALTER is silently accepted and every subsequent INSERT binds `@provider`/`@model` to a non-existent column.
- **Risk:** Corrupted migrations from old DBs look healthy at startup, throw on first INSERT. Tightly coupled to the catch-everything bug above — fixing that surfaces this.
- **Fix:** Drop `NOT NULL` from added columns (make them nullable, default in code) OR stop swallowing `TryAddColumn` errors so startup fails loudly. Preferred: both. No existing test exercises an old-schema DB.

### FTS5 MATCH query unvalidated — user queries with special chars throw SqliteException (severity: medium)

- **Location:** `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs:232-234`; reached from `MemoryIndexService.cs:203, 213`
- **Issue:** The `query` parameter comes straight from `search_memory(query)` the LLM wrote freely and from `/api/memory-index` callers. FTS5 MATCH has a small query grammar (tokens, AND/OR/NOT, "phrase", col:term, term*). Queries with quotes, colons, dashes, parentheses, or even bare ``"what did Alice say?"`` produce `SqliteException: syntax error in FTS5 expression`. `SearchAsync` then throws; the tool returns a server error and the LLM loses trust in `search_memory`.
- **Risk:** Common queries crash the tool. Over time the LLM stops calling it, defeating the feature. Also a minor DoS surface via `/api/memory-index` (behind auth, but still).
- **Fix:** Wrap the user query in `"..."` (phrase mode) after escaping internal double-quotes: `var safe = "\"" + query.Replace("\"", "\"\"") + "\""`. Alternatively catch `SqliteException` in `SearchFts` and return an empty dict.

### `FileConfigStore.Save` has no lock and no tmp-rename — crash/race corrupts config (severity: medium)

- **Location:** `src/agent/OpenAgent.ConfigStore.File/FileConfigStore.cs:38-44`
- **Issue:** Unlike `FileConnectionStore`, `FileConfigStore` has no `SemaphoreSlim` and no `.tmp + rename` pattern. `File.WriteAllBytes(path, bytes)` opens-truncates-writes-closes; concurrent callers torn-write each other, and a process crash mid-write leaves an empty or truncated file. Next startup reads `""`, `JsonDocument.Parse` throws, and the agent cannot start.
- **Risk:** `agent.json` is the source of truth for API key, provider selection, and memory-index config. A corrupted file bricks startup.
- **Fix:** Add the same pattern as `FileConnectionStore`: `SemaphoreSlim` around Load/Save, write to `path + ".tmp"`, then `File.Move(tmp, path, overwrite: true)` (atomic on NTFS/ext4).

### `HuggingFace auto-download — no checksum validation, no resume, no retry (severity: medium)

- **Location:** `src/agent/OpenAgent.Embedding.OnnxMultilingualE5/OnnxMultilingualE5EmbeddingProvider.cs:137-201`; mirrored `OnnxBgeEmbeddingProvider.cs:141-204`
- **Issue:** Both providers fetch raw URLs from `huggingface.co/resolve/main/...` with no integrity check (no SHA256 / model-card hash). The ref is `main`, not a pinned commit — a malicious or accidental upstream change is silently accepted. Also: a 30-min timeout covers large downloads, but if the stream stalls at 90%, the tmp file is deleted and the whole ~2.5 GB multilingual-e5-large download restarts; no resume, no retry.
- **Risk:** Supply-chain integrity gap (lower severity because this is a local tool, not an auto-updating service); bandwidth waste on flaky connections.
- **Fix:** Pin HF ref to a specific revision (`.../resolve/<commit>/onnx/model.onnx`) and store an expected SHA256 alongside the URL so `DownloadIfMissingAsync` can validate post-download. Optionally add `Range: bytes=N-` resume.

### `ActiveSkills` JSON deserialize crashes on corrupted row (severity: low)

- **Location:** `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:558`
- **Issue:** `JsonSerializer.Deserialize<List<string>>(reader.GetString(16))` throws `JsonException` on malformed JSON. One corrupted row (manual edit, interrupted write, encoding drift) poisons every subsequent `Get()` / `GetAll()` for that row; UI conversation list goes blank for that entry.
- **Risk:** Single bad row blocks `/api/conversations`.
- **Fix:** Wrap with `try { ... } catch (JsonException) { /* log, return null list */ }`. Users can re-add skills.

### `Update()` on conversation silently drops `DisplayName` — not in the SET clause (severity: low)

- **Location:** `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:246-288`
- **Issue:** The main `Update(conversation)` does not include `DisplayName = @displayName`. There's a separate `UpdateDisplayName` method at line 568-576. If a caller mutates `conversation.DisplayName` and calls `Update`, the change is silently lost. No throw, no log.
- **Risk:** Subtle data loss via the natural "mutate + save" pattern.
- **Fix:** Include `DisplayName` in the `Update` statement OR make the field init-only on `Conversation`. If the split is deliberate (e.g. channel metadata shouldn't clobber UI edits), document it.

### `MessageModality` retrofit buckets historical voice transcripts as Text (severity: low)

- **Location:** `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:99`
- **Issue:** `TryAddColumn "Modality", "INTEGER NOT NULL DEFAULT 0"` silently sets every pre-existing message to `MessageModality.Text`, including pre-migration voice transcripts. Downstream filters that slice "voice only" will miss old data.
- **Risk:** Historical misattribution; only matters for analytics.
- **Fix:** Accept + document, or add `Modality = 2 /* Unknown */` and backfill.

### `CompactionRunning` flag can get stuck after an abrupt shutdown (severity: low)

- **Location:** `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:404-430`
- **Issue:** `_ = Task.Run(...)` fires-and-forgets the compaction task with no CancellationToken from the host lifetime. If the process dies mid-compaction, `CompactionRunning = 1` persists on disk. On next startup `TryStartCompaction` line 407 `if (conversation.CompactionRunning) return` refuses to retry.
- **Risk:** Stuck-forever lock until manual UPDATE; the conversation's prompt grows without further compaction.
- **Fix:** In `InitializeDatabase` run `UPDATE Conversations SET CompactionRunning = 0` to clear stale locks from the previous run. Mirror how `ScheduledTasks` clears running flags on startup.

### Hosted service startup race — first tick runs before AgentConfig is configured (severity: low)

- **Location:** `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs:55-75`
- **Issue:** `StartAsync` kicks off `LoopAsync` which calls `CheckAndRunAsync` immediately. If `AgentConfig` hasn't been populated by `IConfigurable` wiring yet (it's a shared singleton mutated by `Configure`), the first tick observes `EmbeddingProvider = ""`, logs a one-shot warning, sets `_warnedMissingProvider = true`, and then never re-warns even if the provider arrives later. The second tick comes an hour later.
- **Risk:** User configures a provider 2 seconds after startup; indexing stays idle for an hour with a stale log.
- **Fix:** Drop `_warnedMissingProvider` (accept slight log spam) OR reset it when transitioning non-empty → empty. Alternatively delay the first tick by 30-60 seconds.

### Unparameterized table/column identifiers in `TryAddColumn` (severity: low/informational)

- **Location:** `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs:529`
- **Issue:** `cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}"` concatenates `table`, `column`, `type` into SQL. SQLite's parameter binding doesn't support DDL identifiers, so string interpolation is the only option — but this should be explicit that `table/column/type` are compile-time constants, never user input. All current callers satisfy that, but future maintenance could accidentally pass dynamic strings.
- **Risk:** Low; all call sites are literal string constants today.
- **Fix:** Add an XML comment `/// WARNING: table/column/type must be compile-time constants — SQL identifier injection is not prevented.`

### `FileConnectionStore.LoadAll` reads the whole file on every call — N+1 risk (severity: low)

- **Location:** `src/agent/OpenAgent.ConfigStore.File/FileConnectionStore.cs:28-38, 42-53`
- **Issue:** Every `Load(id)` and `LoadAll()` reads + JSON-parses the whole file. At a handful of connections this is free; at 50+ it's noticeable per inbound message.
- **Risk:** Performance cliff only; not user-visible until dozens of channels.
- **Fix:** Cache `connections` in memory, invalidate on Save/Delete. Defer until a real problem is seen.

## Smells

### `ReadMessagesFromDb` opens a fresh connection inside `GetMessages` which already opened one (severity: medium)

- **Location:** `SqliteConversationStore.cs:351-370` + `484-508`
- **Issue:** `GetMessages` calls `Get()` (connection #1, open+close), then `ReadMessagesFromDb` (connection #2, open+close). Two pool acquisitions per logical op. Similar in `Update` → `UpdateCompactionState`. Connection-per-call is the pattern, but composites pay 2-3× the connection churn.
- **Suggestion:** Add internal `*WithConnection(SqliteConnection, ...)` overloads so public methods can share a connection when composing. Also consider a real transaction around `Update + TryStartCompaction`.

### Connection pragma redundantly re-runs `PRAGMA journal_mode=WAL` on every Open (severity: low)

- **Location:** `SqliteConversationStore.cs:510-521`
- **Issue:** `journal_mode` is a database-level persistent setting; running it on every open is waste (but harmless). `foreign_keys` must stay per-connection. With connection-per-call, hot paths pay this cost thousands of times.
- **Suggestion:** Move `PRAGMA journal_mode=WAL` into `InitializeDatabase` once. Keep `foreign_keys=ON` on every open.

### Hard-coded hybrid search weights (0.7 / 0.3) and fixed 512 max sequence length (severity: low)

- **Location:** `MemoryIndexService.cs:36-37`; `OnnxMultilingualE5EmbeddingProvider.cs:37`
- **Issue:** Reasonable defaults, but not configurable. The 512 truncation at embedding time is silent — a long chunk drops its tail. `MemoryChunker` imposes no size limit on chunks, so truncation can happen invisibly.
- **Suggestion:** Log a warning when `rawIds.Count > maxBodyTokens`. Leave weights hard-coded until there's measurable need.

### `LoadMemoryChunksTool` parses `ids` without guarding integer parse errors or upper-bounding the array (severity: low)

- **Location:** `MemoryToolHandler.cs:98-102`
- **Issue:** `el.GetInt32()` throws on non-integer elements; no upper-bound on the array (`10,000 ids => 10,000 rows returned to the LLM context => token explosion`).
- **Suggestion:** Use `el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)`, skip non-ints, cap at ~50 results, return an `error` field when zero valid ids remain.

### `MemoryIndexService` chunk cache is unbounded (severity: low)

- **Location:** `MemoryIndexService.cs:262-274`
- **Issue:** The cache holds every chunk for the active (provider, model). At 768 dims × 4 bytes = 3 KB/vector, 100K chunks is ~300 MB; no eviction.
- **Suggestion:** Acceptable today given the size range; note the upper bound in a code comment and revisit if memory growth becomes an issue. Long-term: fetch top-K via SQL vector distance (needs sqlite-vec).

### Duplicate download helper code between E5 and BGE providers (severity: low)

- **Location:** `OnnxMultilingualE5EmbeddingProvider.cs:137-201`, `OnnxBgeEmbeddingProvider.cs:141-204`
- **Issue:** `DownloadIfMissingAsync` and `CopyWithProgressAsync` are byte-for-byte the same. CLAUDE.md memory explicitly says "no shared base class — copy the nearest sibling." OK, but extracting a static helper would cut ~40 lines per provider and standardize retry/resume once added.
- **Suggestion:** Respect the current policy; revisit at the third provider.

### Chunker stores content-only — no date or back-pointer in the chunk body itself (severity: low)

- **Location:** `MemoryChunker.cs:33-50`
- **Issue:** The LLM chunks the file into topic bodies. The DB row has `date` as a column but the chunk text has nothing tying it to a date. `search_memory` / `load_memory_chunks` responses include `date` in the JSON, so the LLM does see it — verify the agent prompt handles chronology properly.
- **Suggestion:** Accept as-is; confirm `date` in response JSON is surfaced clearly enough. No action needed unless the agent gets confused on temporal queries.

### `MemoryIndexEndpoints` `/run` not throttled (severity: low)

- **Location:** `MemoryIndexEndpoints.cs:16-19`
- **Issue:** Auth-required but not rate-limited. A compromised key could hammer `/run`; each run triggers LLM + embedding calls. `_runLock` serializes them, but doesn't reject.
- **Suggestion:** `TryWaitAsync(0)` on the semaphore and return `409 Conflict` if another run is in-flight, or add a trivial 1/min `DateTimeOffset _lastRun` throttle.

### Logging: nothing sensitive logged — good (severity: informational)

- **Location:** `MemoryIndexService.cs:98, 142, 165`; `FileConfigStore.cs:28-44`; all store logs
- **Note:** All logs name files / keys / counts, never bodies. `FileConfigStore.Configure` correctly does nothing. Keep it this way.

## Open Questions

1. **Is `memory/backup/` path ever re-loaded?** The prompt loader's `????-??-??.md` glob is scoped to `memory/` root, so `backup/` is ignored. Confirmed. But nothing enforces that — a rebuild tool or future feature that copies files back from `backup/` into `memory/` root would hit `UNIQUE(date, chunk_index)`. Worth adding an assertion or rebuild helper that honors the invariant.
2. **What happens when `AgentConfig.EmbeddingModel` changes at runtime?** The provider is a keyed singleton, `_model` is readonly, so changing the config does nothing until restart — docs say "requires restart." Chunks stay stamped with the old model, so searches against the old chunks fall back to FTS only. Subtle but correct. Should a model change trigger a re-index of old chunks?
3. **`CompactionRunning` lock has no TTL.** If a compaction task hangs (LLM provider stalls indefinitely), the lock persists until the task throws. Should `RunCompactionAsync` enforce a CancellationToken with a hard cap?
4. **Shutdown cancellation of in-flight LLM calls.** `RunAsync(ct)` threads `ct` all the way down to `provider.CompleteAsync`. On shutdown the HTTP client *should* abort the stream — worth verifying with a small integration test.
5. **`MemoryDays` hard minimum of 1 — document?** `Math.Max(1, _agentConfig.MemoryDays)` forces at least 1 day in the prompt. Presumably intentional. Document: "setting 0 still keeps today's file."
6. **No test exercises the ALTER TABLE migration path.** Tests create a fresh DB every time. There is no test opening an old-schema DB and verifying the migration runs cleanly. Consider seeding an old schema to catch future `NOT NULL DEFAULT` regressions.
7. **Should `FileConnectionStore.LoadAll` cache?** N+1 IO risk noted above; no test exercises it.

## Files reviewed

- `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- `src/agent/OpenAgent.ConversationStore.Sqlite/OpenAgent.ConversationStore.Sqlite.csproj`
- `src/agent/OpenAgent.MemoryIndex/MemoryChunkStore.cs`
- `src/agent/OpenAgent.MemoryIndex/MemoryChunker.cs`
- `src/agent/OpenAgent.MemoryIndex/MemoryIndexService.cs`
- `src/agent/OpenAgent.MemoryIndex/MemoryIndexHostedService.cs`
- `src/agent/OpenAgent.MemoryIndex/MemoryIndexEndpoints.cs`
- `src/agent/OpenAgent.MemoryIndex/MemoryToolHandler.cs`
- `src/agent/OpenAgent.MemoryIndex/ServiceCollectionExtensions.cs`
- `src/agent/OpenAgent.MemoryIndex/OpenAgent.MemoryIndex.csproj`
- `src/agent/OpenAgent.Embedding.OnnxMultilingualE5/OnnxMultilingualE5EmbeddingProvider.cs`
- `src/agent/OpenAgent.Embedding.OnnxMultilingualE5/OpenAgent.Embedding.OnnxMultilingualE5.csproj`
- `src/agent/OpenAgent.Embedding.OnnxBge/OnnxBgeEmbeddingProvider.cs`
- `src/agent/OpenAgent.Embedding.OnnxBge/OpenAgent.Embedding.OnnxBge.csproj`
- `src/agent/OpenAgent.ConfigStore.File/FileConfigStore.cs`
- `src/agent/OpenAgent.ConfigStore.File/FileConnectionStore.cs`
- `src/agent/OpenAgent.Contracts/IEmbeddingProvider.cs`
- `src/agent/OpenAgent.Contracts/IConfigStore.cs`
- `src/agent/OpenAgent.Contracts/IConnectionStore.cs`
- `src/agent/OpenAgent.Contracts/IConversationStore.cs`
- `src/agent/OpenAgent.Contracts/AgentEnvironment.cs`
- `src/agent/OpenAgent.Models/Configs/AgentConfig.cs`
- `src/agent/OpenAgent.Models/Conversations/Conversation.cs`
- `src/agent/OpenAgent.Models/Conversations/Message.cs`
- `src/agent/OpenAgent.Models/Conversations/CompactionConfig.cs`
- `docs/memory/DESIGN.md`
- Cross-referenced: `src/agent/OpenAgent/Program.cs:140-156` (DI wiring)
- Tests reviewed: `MemoryChunkStoreTests.cs`, `MemoryIndexServiceTests.cs`, `MemoryIndexHostedServiceTests.cs`, `MemoryToolHandlerTests.cs`, `MemoryChunkerTests.cs`, `OnnxMultilingualE5EmbeddingProviderTests.cs`, `OnnxBgeEmbeddingProviderTests.cs`, `SqliteConversationStoreTests.cs`
- Grep confirmed: no `SqliteConnection`/`SqliteCommand`/`IDbConnection` usage outside `ConversationStore.Sqlite` and `MemoryIndex`.
