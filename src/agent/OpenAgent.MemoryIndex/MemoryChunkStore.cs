using System.Buffers.Binary;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Input record for InsertChunks — the chunk's content, summary, and embedding vector.
/// </summary>
public sealed record ChunkEntry(string Content, string Summary, float[] Embedding);

/// <summary>
/// A chunk row as read back from the database.
/// </summary>
public sealed record StoredChunk(
    int Id,
    string Date,
    int ChunkIndex,
    string Content,
    string Summary,
    float[] Embedding,
    string Provider,
    string Model,
    int Dimensions);

/// <summary>
/// Aggregate statistics about the indexed chunks.
/// </summary>
public sealed record ChunkStats(
    [property: JsonPropertyName("totalChunks")] int TotalChunks,
    [property: JsonPropertyName("totalDays")] int TotalDays,
    [property: JsonPropertyName("oldestDate")] string? OldestDate,
    [property: JsonPropertyName("newestDate")] string? NewestDate);

/// <summary>
/// SQLite-backed store for memory chunks. Persists content, summary, embedding, and
/// provider metadata in `memory_chunks` and maintains a parallel FTS5 virtual table
/// for keyword search. Vector similarity is computed in-process on cached embeddings.
/// </summary>
public sealed class MemoryChunkStore
{
    private readonly string _connectionString;

    /// <summary>Public constructor — database lives at {dataPath}/memory.db.</summary>
    public MemoryChunkStore(AgentEnvironment environment)
        : this(Path.Combine(environment.DataPath, "memory.db"))
    {
    }

    internal MemoryChunkStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_chunks (
                id          INTEGER PRIMARY KEY,
                date        TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                content     TEXT NOT NULL,
                summary     TEXT NOT NULL,
                embedding   BLOB NOT NULL,
                provider    TEXT NOT NULL,
                model       TEXT NOT NULL,
                dimensions  INTEGER NOT NULL,
                UNIQUE(date, chunk_index)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS memory_chunks_fts USING fts5(
                summary, content, content=memory_chunks, content_rowid=id
            );

            -- Clean up job_state from earlier "run once per day" guard. The hosted service
            -- now just calls RunAsync every hour and relies on RunAsync's natural idempotency
            -- (alreadyProcessed check) to avoid duplicate work.
            DROP TABLE IF EXISTS job_state;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Atomically insert all chunks for a given date, provider, and model. Writes to both the
    /// main table and the FTS5 index inside one transaction so the two never drift.
    /// </summary>
    public void InsertChunks(string date, string provider, string model, int dimensions, IReadOnlyList<ChunkEntry> entries)
    {
        if (entries.Count == 0) return;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            // Insert into main table, capture generated id for the matching FTS row
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO memory_chunks (date, chunk_index, content, summary, embedding, provider, model, dimensions)
                VALUES (@date, @chunk_index, @content, @summary, @embedding, @provider, @model, @dimensions);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("@date", date);
            insert.Parameters.AddWithValue("@chunk_index", i);
            insert.Parameters.AddWithValue("@content", entry.Content);
            insert.Parameters.AddWithValue("@summary", entry.Summary);
            insert.Parameters.AddWithValue("@embedding", SerializeEmbedding(entry.Embedding));
            insert.Parameters.AddWithValue("@provider", provider);
            insert.Parameters.AddWithValue("@model", model);
            insert.Parameters.AddWithValue("@dimensions", dimensions);

            var rowId = (long)insert.ExecuteScalar()!;

            // Mirror to FTS5 external-content table using the same rowid
            using var fts = connection.CreateCommand();
            fts.Transaction = tx;
            fts.CommandText = """
                INSERT INTO memory_chunks_fts (rowid, summary, content)
                VALUES (@rowid, @summary, @content);
                """;
            fts.Parameters.AddWithValue("@rowid", rowId);
            fts.Parameters.AddWithValue("@summary", entry.Summary);
            fts.Parameters.AddWithValue("@content", entry.Content);
            fts.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Distinct dates that already have any chunks in the store.</summary>
    public HashSet<string> GetProcessedDates()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT date FROM memory_chunks;";

        var dates = new HashSet<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            dates.Add(reader.GetString(0));
        return dates;
    }

    /// <summary>
    /// Load every chunk embedded by the given provider + model, ordered by date then chunk_index.
    /// Intended for vector search — chunks from other providers/models have an incompatible
    /// vector space and are filtered out here so cosine math never crosses spaces.
    /// </summary>
    public List<StoredChunk> GetAllChunks(string provider, string model)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, date, chunk_index, content, summary, embedding, provider, model, dimensions
            FROM memory_chunks
            WHERE provider = @provider AND model = @model
            ORDER BY date, chunk_index;
            """;
        cmd.Parameters.AddWithValue("@provider", provider);
        cmd.Parameters.AddWithValue("@model", model);

        var chunks = new List<StoredChunk>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(ReadChunk(reader, includeEmbedding: true));
        }
        return chunks;
    }

    /// <summary>
    /// Load specific chunks by ID. Not filtered by provider — load_memory_chunks returns raw
    /// content regardless of which embedding produced the row. Embeddings aren't returned.
    /// </summary>
    public List<StoredChunk> GetChunksByIds(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return [];

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();

        // Build a parameterized IN clause dynamically so callers can't inject SQL
        var paramNames = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            paramNames[i] = $"@id{i}";
            cmd.Parameters.AddWithValue(paramNames[i], ids[i]);
        }
        cmd.CommandText = $"""
            SELECT id, date, chunk_index, content, summary, provider, model, dimensions
            FROM memory_chunks
            WHERE id IN ({string.Join(", ", paramNames)})
            ORDER BY date, chunk_index;
            """;

        var chunks = new List<StoredChunk>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(ReadChunk(reader, includeEmbedding: false));
        }
        return chunks;
    }

    /// <summary>
    /// Run an FTS5 MATCH query and return a map of chunk id → normalized score.
    /// Normalization: abs(rank) / (1 + abs(rank)) so better matches (rank more negative)
    /// yield higher scores, bounded to [0, 1). Chunks not returned by FTS5 are absent.
    /// </summary>
    public Dictionary<int, float> SearchFts(string query)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT rowid, rank FROM memory_chunks_fts WHERE memory_chunks_fts MATCH @query;
            """;
        cmd.Parameters.AddWithValue("@query", query);

        var scores = new Dictionary<int, float>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = (int)reader.GetInt64(0);
            var rank = reader.GetDouble(1);
            var absRank = Math.Abs(rank);
            var normalized = (float)(absRank / (1.0 + absRank));
            scores[id] = normalized;
        }
        return scores;
    }

    /// <summary>Aggregate counts and date range across all chunks.</summary>
    public ChunkStats GetStats()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT date), MIN(date), MAX(date) FROM memory_chunks;
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new ChunkStats(0, 0, null, null);

        var total = (int)reader.GetInt64(0);
        var days = (int)reader.GetInt64(1);
        var oldest = reader.IsDBNull(2) ? null : reader.GetString(2);
        var newest = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new ChunkStats(total, days, oldest, newest);
    }

    private static StoredChunk ReadChunk(SqliteDataReader reader, bool includeEmbedding)
    {
        var id = (int)reader.GetInt64(0);
        var date = reader.GetString(1);
        var chunkIndex = (int)reader.GetInt64(2);
        var content = reader.GetString(3);
        var summary = reader.GetString(4);

        if (includeEmbedding)
        {
            // column order: id, date, chunk_index, content, summary, embedding, provider, model, dimensions
            var blob = (byte[])reader["embedding"];
            var provider = reader.GetString(6);
            var model = reader.GetString(7);
            var dimensions = (int)reader.GetInt64(8);
            return new StoredChunk(id, date, chunkIndex, content, summary, DeserializeEmbedding(blob), provider, model, dimensions);
        }
        else
        {
            // column order: id, date, chunk_index, content, summary, provider, model, dimensions
            var provider = reader.GetString(5);
            var model = reader.GetString(6);
            var dimensions = (int)reader.GetInt64(7);
            return new StoredChunk(id, date, chunkIndex, content, summary, [], provider, model, dimensions);
        }
    }

    /// <summary>
    /// Cosine similarity of two equally-sized vectors. Returns 0 for mismatched lengths
    /// or any zero-magnitude vector (rather than NaN).
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0f;
        return (float)(dot / (Math.Sqrt(magA) * Math.Sqrt(magB)));
    }

    /// <summary>Serialize a float vector to little-endian bytes for BLOB storage.</summary>
    public static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        var span = bytes.AsSpan();
        for (var i = 0; i < embedding.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[(i * sizeof(float))..], embedding[i]);
        }
        return bytes;
    }

    /// <summary>Deserialize bytes produced by SerializeEmbedding back into a float vector.</summary>
    public static float[] DeserializeEmbedding(byte[] blob)
    {
        var count = blob.Length / sizeof(float);
        var embedding = new float[count];
        var span = blob.AsSpan();
        for (var i = 0; i < count; i++)
        {
            embedding[i] = BinaryPrimitives.ReadSingleLittleEndian(span[(i * sizeof(float))..]);
        }
        return embedding;
    }
}
