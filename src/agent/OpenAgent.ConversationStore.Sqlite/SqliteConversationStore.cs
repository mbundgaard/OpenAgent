using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.ConversationStore.Sqlite;

/// <summary>
/// SQLite-backed conversation store. Database file lives at {dataPath}/conversations.db.
/// </summary>
public sealed class SqliteConversationStore : IConversationStore, IDisposable
{
    private readonly ILogger<SqliteConversationStore> _logger;
    private readonly string _connectionString;
    private readonly CompactionConfig _compactionConfig;
    private readonly ICompactionSummarizer? _compactionSummarizer;

    public SqliteConversationStore(
        AgentEnvironment environment,
        ILogger<SqliteConversationStore> logger,
        CompactionConfig compactionConfig,
        ICompactionSummarizer? compactionSummarizer = null)
    {
        _logger = logger;
        _compactionConfig = compactionConfig;
        _compactionSummarizer = compactionSummarizer;
        var dbPath = Path.Combine(environment.DataPath, "conversations.db");
        _connectionString = $"Data Source={dbPath}";

        InitializeDatabase();
    }

    public string Key => "conversation-store";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } = [];

    public void Configure(JsonElement configuration)
    {
    }

    /// <summary>Creates the tables if they don't exist.</summary>
    private void InitializeDatabase()
    {
        using var connection = Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Conversations (
                Id TEXT PRIMARY KEY,
                Source TEXT NOT NULL,
                Type INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                VoiceSessionId TEXT,
                VoiceSessionOpen INTEGER NOT NULL DEFAULT 0,
                LastPromptTokens INTEGER
            );

            CREATE TABLE IF NOT EXISTS Messages (
                Id TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                Role TEXT NOT NULL,
                Content TEXT,
                CreatedAt TEXT NOT NULL,
                ToolCalls TEXT,
                ToolCallId TEXT,
                ChannelMessageId TEXT,
                ReplyToChannelMessageId TEXT,
                FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_Messages_ConversationId ON Messages(ConversationId);
            """;
        cmd.ExecuteNonQuery();

        // Migrate existing databases — add columns that may not exist yet
        TryAddColumn(connection, "Messages", "ToolCalls", "TEXT");
        TryAddColumn(connection, "Messages", "ToolCallId", "TEXT");
        TryAddColumn(connection, "Messages", "ChannelMessageId", "TEXT");
        TryAddColumn(connection, "Messages", "ReplyToChannelMessageId", "TEXT");
        TryAddColumn(connection, "Conversations", "LastPromptTokens", "INTEGER");
        TryAddColumn(connection, "Conversations", "Context", "TEXT");
        TryAddColumn(connection, "Conversations", "CompactedUpToRowId", "INTEGER");
        TryAddColumn(connection, "Conversations", "CompactionRunning", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "Conversations", "Provider", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(connection, "Conversations", "Model", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(connection, "Conversations", "TotalPromptTokens", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "Conversations", "TotalCompletionTokens", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "Conversations", "TurnCount", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "Conversations", "LastActivity", "TEXT");
        TryAddColumn(connection, "Messages", "PromptTokens", "INTEGER");
        TryAddColumn(connection, "Messages", "CompletionTokens", "INTEGER");
        TryAddColumn(connection, "Messages", "ElapsedMs", "INTEGER");
        TryAddColumn(connection, "Conversations", "ActiveSkills", "TEXT");
        TryAddColumn(connection, "Conversations", "ChannelType", "TEXT");
        TryAddColumn(connection, "Conversations", "ConnectionId", "TEXT");
        TryAddColumn(connection, "Conversations", "ChannelChatId", "TEXT");
        TryAddColumn(connection, "Messages", "Modality", "INTEGER NOT NULL DEFAULT 0");

        _logger.LogInformation("SQLite conversation store initialized at {ConnectionString}", _connectionString);
    }

    public Conversation GetOrCreate(string conversationId, string source, ConversationType type, string provider, string model)
    {
        // Try to get existing first
        var existing = Get(conversationId);
        if (existing is not null)
            return existing;

        // Create new conversation
        var conversation = new Conversation
        {
            Id = conversationId,
            Source = source,
            Type = type,
            Provider = provider,
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow
        };

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Conversations (Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, Provider, Model, ActiveSkills, ChannelType, ConnectionId, ChannelChatId)
            VALUES (@id, @source, @type, @createdAt, @voiceSessionId, @voiceSessionOpen, @provider, @model, @activeSkills, @channelType, @connectionId, @channelChatId)
            """;
        cmd.Parameters.AddWithValue("@id", conversation.Id);
        cmd.Parameters.AddWithValue("@source", conversation.Source);
        cmd.Parameters.AddWithValue("@type", (int)conversation.Type);
        cmd.Parameters.AddWithValue("@createdAt", conversation.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@voiceSessionId", (object?)conversation.VoiceSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@voiceSessionOpen", conversation.VoiceSessionOpen ? 1 : 0);
        cmd.Parameters.AddWithValue("@provider", conversation.Provider);
        cmd.Parameters.AddWithValue("@model", conversation.Model);
        cmd.Parameters.AddWithValue("@activeSkills",
            conversation.ActiveSkills is not null
                ? (object)JsonSerializer.Serialize(conversation.ActiveSkills)
                : DBNull.Value);
        cmd.Parameters.AddWithValue("@channelType", (object?)conversation.ChannelType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@connectionId", (object?)conversation.ConnectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@channelChatId", (object?)conversation.ChannelChatId ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        // Re-read in case of a race (INSERT OR IGNORE means another thread may have created it)
        return Get(conversationId) ?? conversation;
    }

    public Conversation? FindChannelConversation(string channelType, string connectionId, string channelChatId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning, Provider, Model, TotalPromptTokens, TotalCompletionTokens, TurnCount, LastActivity, ActiveSkills, ChannelType, ConnectionId, ChannelChatId FROM Conversations
            WHERE ChannelType = @channelType AND ConnectionId = @connectionId AND ChannelChatId = @channelChatId
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@channelType", channelType);
        cmd.Parameters.AddWithValue("@connectionId", connectionId);
        cmd.Parameters.AddWithValue("@channelChatId", channelChatId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public Conversation FindOrCreateChannelConversation(
        string channelType,
        string connectionId,
        string channelChatId,
        string source,
        ConversationType type,
        string provider,
        string model)
    {
        // Look up by channel binding first
        var existing = FindChannelConversation(channelType, connectionId, channelChatId);
        if (existing is not null)
            return existing;

        // Not found — create a new one with a fresh GUID
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString(),
            Source = source,
            Type = type,
            Provider = provider,
            Model = model,
            ChannelType = channelType,
            ConnectionId = connectionId,
            ChannelChatId = channelChatId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        using (var connection = Open())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO Conversations (Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, Provider, Model, ActiveSkills, ChannelType, ConnectionId, ChannelChatId)
                VALUES (@id, @source, @type, @createdAt, @voiceSessionId, @voiceSessionOpen, @provider, @model, @activeSkills, @channelType, @connectionId, @channelChatId)
                """;
            cmd.Parameters.AddWithValue("@id", conversation.Id);
            cmd.Parameters.AddWithValue("@source", conversation.Source);
            cmd.Parameters.AddWithValue("@type", (int)conversation.Type);
            cmd.Parameters.AddWithValue("@createdAt", conversation.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@voiceSessionId", DBNull.Value);
            cmd.Parameters.AddWithValue("@voiceSessionOpen", 0);
            cmd.Parameters.AddWithValue("@provider", conversation.Provider);
            cmd.Parameters.AddWithValue("@model", conversation.Model);
            cmd.Parameters.AddWithValue("@activeSkills", DBNull.Value);
            cmd.Parameters.AddWithValue("@channelType", channelType);
            cmd.Parameters.AddWithValue("@connectionId", connectionId);
            cmd.Parameters.AddWithValue("@channelChatId", channelChatId);
            cmd.ExecuteNonQuery();
        }

        return conversation;
    }

    public IReadOnlyList<Conversation> GetAll()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning, Provider, Model, TotalPromptTokens, TotalCompletionTokens, TurnCount, LastActivity, ActiveSkills, ChannelType, ConnectionId, ChannelChatId FROM Conversations ORDER BY CreatedAt DESC";

        using var reader = cmd.ExecuteReader();
        var list = new List<Conversation>();
        while (reader.Read())
            list.Add(ReadConversation(reader));

        return list;
    }

    public Conversation? Get(string conversationId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning, Provider, Model, TotalPromptTokens, TotalCompletionTokens, TurnCount, LastActivity, ActiveSkills, ChannelType, ConnectionId, ChannelChatId FROM Conversations WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", conversationId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public void Update(Conversation conversation)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Conversations
            SET Source = @source, Type = @type, VoiceSessionId = @voiceSessionId,
                VoiceSessionOpen = @voiceSessionOpen, LastPromptTokens = @lastPromptTokens,
                Context = @context, CompactedUpToRowId = @compactedUpToRowId,
                CompactionRunning = @compactionRunning, Provider = @provider, Model = @model,
                TotalPromptTokens = @totalPromptTokens, TotalCompletionTokens = @totalCompletionTokens,
                TurnCount = @turnCount, LastActivity = @lastActivity, ActiveSkills = @activeSkills,
                ChannelType = @channelType, ConnectionId = @connectionId, ChannelChatId = @channelChatId
            WHERE Id = @id
            """;
        cmd.Parameters.AddWithValue("@id", conversation.Id);
        cmd.Parameters.AddWithValue("@source", conversation.Source);
        cmd.Parameters.AddWithValue("@type", (int)conversation.Type);
        cmd.Parameters.AddWithValue("@voiceSessionId", (object?)conversation.VoiceSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@voiceSessionOpen", conversation.VoiceSessionOpen ? 1 : 0);
        cmd.Parameters.AddWithValue("@lastPromptTokens", (object?)conversation.LastPromptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@context", (object?)conversation.Context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@compactedUpToRowId", (object?)conversation.CompactedUpToRowId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@compactionRunning", conversation.CompactionRunning ? 1 : 0);
        cmd.Parameters.AddWithValue("@provider", conversation.Provider);
        cmd.Parameters.AddWithValue("@model", conversation.Model);
        cmd.Parameters.AddWithValue("@totalPromptTokens", conversation.TotalPromptTokens);
        cmd.Parameters.AddWithValue("@totalCompletionTokens", conversation.TotalCompletionTokens);
        cmd.Parameters.AddWithValue("@turnCount", conversation.TurnCount);
        cmd.Parameters.AddWithValue("@lastActivity", (object?)conversation.LastActivity?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@activeSkills",
            conversation.ActiveSkills is not null
                ? (object)JsonSerializer.Serialize(conversation.ActiveSkills)
                : DBNull.Value);
        cmd.Parameters.AddWithValue("@channelType", (object?)conversation.ChannelType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@connectionId", (object?)conversation.ConnectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@channelChatId", (object?)conversation.ChannelChatId ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        TryStartCompaction(conversation);
    }

    public void UpdateType(string conversationId, ConversationType type)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Conversations SET Type = @type WHERE Id = @id AND Type != @type";
        cmd.Parameters.AddWithValue("@id", conversationId);
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.ExecuteNonQuery();
    }

    public bool Delete(string conversationId)
    {
        using var connection = Open();

        // Delete messages first (FK cascade may not be enforced without PRAGMA)
        using var msgCmd = connection.CreateCommand();
        msgCmd.CommandText = "DELETE FROM Messages WHERE ConversationId = @id";
        msgCmd.Parameters.AddWithValue("@id", conversationId);
        msgCmd.ExecuteNonQuery();

        // Delete conversation
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Conversations WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", conversationId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void AddMessage(string conversationId, Message message)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Messages (Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality)
            VALUES (@id, @conversationId, @role, @content, @createdAt, @toolCalls, @toolCallId, @channelMessageId, @replyToChannelMessageId, @promptTokens, @completionTokens, @elapsedMs, @modality)
            """;
        cmd.Parameters.AddWithValue("@id", message.Id);
        cmd.Parameters.AddWithValue("@conversationId", message.ConversationId);
        cmd.Parameters.AddWithValue("@role", message.Role);
        cmd.Parameters.AddWithValue("@content", (object?)message.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", message.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@toolCalls", (object?)message.ToolCalls ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@toolCallId", (object?)message.ToolCallId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@channelMessageId", (object?)message.ChannelMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@replyToChannelMessageId", (object?)message.ReplyToChannelMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@promptTokens", (object?)message.PromptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@completionTokens", (object?)message.CompletionTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@elapsedMs", (object?)message.ElapsedMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modality", (int)message.Modality);
        cmd.ExecuteNonQuery();
    }

    public void UpdateChannelMessageId(string messageId, string channelMessageId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET ChannelMessageId = @channelMessageId WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", messageId);
        cmd.Parameters.AddWithValue("@channelMessageId", channelMessageId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Message> GetMessages(string conversationId)
    {
        var conversation = Get(conversationId);
        var list = new List<Message>();

        // Prepend compaction summary as a system message if present
        if (conversation?.Context is not null)
        {
            list.Add(new Message
            {
                Id = "context",
                ConversationId = conversationId,
                Role = "system",
                Content = conversation.Context
            });
        }

        list.AddRange(ReadMessagesFromDb(conversationId, conversation?.CompactedUpToRowId));
        return list;
    }

    public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0) return [];

        using var connection = Open();
        using var cmd = connection.CreateCommand();

        var paramNames = messageIds.Select((_, i) => $"@id{i}").ToList();
        cmd.CommandText = $"SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality FROM Messages WHERE Id IN ({string.Join(", ", paramNames)}) ORDER BY rowid";

        for (var i = 0; i < messageIds.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], messageIds[i]);

        using var reader = cmd.ExecuteReader();
        var list = new List<Message>();
        while (reader.Read())
        {
            list.Add(ReadMessage(reader));
        }

        return list;
    }

    public void Dispose()
    {
        // No persistent connection to dispose — each operation opens/closes its own
    }

    /// <summary>
    /// Checks if compaction should run and starts it in the background if so.
    /// Called from Update() when LastPromptTokens is set.
    /// </summary>
    private void TryStartCompaction(Conversation conversation)
    {
        if (_compactionSummarizer is null) return;
        if (conversation.CompactionRunning) return;
        if (conversation.LastPromptTokens is null) return;
        if (conversation.LastPromptTokens.Value < _compactionConfig.TriggerThreshold) return;

        // Set lock
        conversation.CompactionRunning = true;
        UpdateCompactionState(conversation.Id, compactionRunning: true, context: null, compactedUpToRowId: null);

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCompactionAsync(conversation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compaction failed for conversation {ConversationId}", conversation.Id);
            }
            finally
            {
                UpdateCompactionState(conversation.Id, compactionRunning: false, context: null, compactedUpToRowId: null);
            }
        });
    }

    private async Task RunCompactionAsync(Conversation conversation)
    {
        var liveMessages = ReadMessagesFromDb(conversation.Id, conversation.CompactedUpToRowId);

        var keepCount = _compactionConfig.KeepLatestMessagePairs * 2;
        if (liveMessages.Count <= keepCount)
        {
            _logger.LogDebug("Not enough messages to compact for conversation {ConversationId}", conversation.Id);
            return;
        }

        var toCompact = liveMessages.GetRange(0, liveMessages.Count - keepCount);
        var newCutoffRowId = toCompact[^1].RowId;

        _logger.LogInformation("Compacting {Count} messages for conversation {ConversationId}, cutoff rowid {RowId}",
            toCompact.Count, conversation.Id, newCutoffRowId);

        var result = await _compactionSummarizer!.SummarizeAsync(conversation.Context, toCompact);

        UpdateCompactionState(conversation.Id, compactionRunning: false, context: result.Context, compactedUpToRowId: newCutoffRowId);

        _logger.LogInformation("Compaction complete for conversation {ConversationId}, context length {Length} chars",
            conversation.Id, result.Context.Length);
    }

    /// <summary>Updates compaction-related fields on a conversation. Null values mean "don't change".</summary>
    private void UpdateCompactionState(string conversationId, bool compactionRunning, string? context, long? compactedUpToRowId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();

        var setClauses = new List<string> { "CompactionRunning = @running" };
        cmd.Parameters.AddWithValue("@running", compactionRunning ? 1 : 0);

        if (context is not null)
        {
            setClauses.Add("Context = @context");
            cmd.Parameters.AddWithValue("@context", context);
        }

        if (compactedUpToRowId is not null)
        {
            setClauses.Add("CompactedUpToRowId = @cutoff");
            cmd.Parameters.AddWithValue("@cutoff", compactedUpToRowId.Value);
        }

        cmd.CommandText = $"UPDATE Conversations SET {string.Join(", ", setClauses)} WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", conversationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reads messages from the database, optionally filtering by rowid cutoff.</summary>
    private List<Message> ReadMessagesFromDb(string conversationId, long? afterRowId = null)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();

        if (afterRowId is not null)
        {
            cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality FROM Messages WHERE ConversationId = @id AND rowid > @cutoff ORDER BY rowid";
            cmd.Parameters.AddWithValue("@cutoff", afterRowId.Value);
        }
        else
        {
            cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality FROM Messages WHERE ConversationId = @id ORDER BY rowid";
        }
        cmd.Parameters.AddWithValue("@id", conversationId);

        using var reader = cmd.ExecuteReader();
        var list = new List<Message>();
        while (reader.Read())
        {
            list.Add(ReadMessage(reader));
        }

        return list;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Enable WAL mode for better concurrent read performance
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>Adds a column to an existing table, ignoring if it already exists.</summary>
    private static void TryAddColumn(SqliteConnection connection, string table, string column, string type)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Column already exists — safe to ignore
        }
    }

    private static Conversation ReadConversation(SqliteDataReader reader)
    {
        return new Conversation
        {
            Id = reader.GetString(0),
            Source = reader.GetString(1),
            Type = (ConversationType)reader.GetInt32(2),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(3)),
            VoiceSessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
            VoiceSessionOpen = reader.GetInt32(5) != 0,
            LastPromptTokens = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Context = reader.IsDBNull(7) ? null : reader.GetString(7),
            CompactedUpToRowId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            CompactionRunning = !reader.IsDBNull(9) && reader.GetInt32(9) != 0,
            Provider = reader.GetString(10),
            Model = reader.GetString(11),
            TotalPromptTokens = reader.GetInt64(12),
            TotalCompletionTokens = reader.GetInt64(13),
            TurnCount = reader.GetInt32(14),
            LastActivity = reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)),
            ActiveSkills = reader.IsDBNull(16) ? null : JsonSerializer.Deserialize<List<string>>(reader.GetString(16)),
            ChannelType = reader.IsDBNull(17) ? null : reader.GetString(17),
            ConnectionId = reader.IsDBNull(18) ? null : reader.GetString(18),
            ChannelChatId = reader.IsDBNull(19) ? null : reader.GetString(19)
        };
    }

    private static Message ReadMessage(SqliteDataReader reader)
    {
        return new Message
        {
            RowId = reader.GetInt64(0),
            Id = reader.GetString(1),
            ConversationId = reader.GetString(2),
            Role = reader.GetString(3),
            Content = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            ToolCalls = reader.IsDBNull(6) ? null : reader.GetString(6),
            ToolCallId = reader.IsDBNull(7) ? null : reader.GetString(7),
            ChannelMessageId = reader.IsDBNull(8) ? null : reader.GetString(8),
            ReplyToChannelMessageId = reader.IsDBNull(9) ? null : reader.GetString(9),
            PromptTokens = reader.IsDBNull(10) ? null : reader.GetInt32(10),
            CompletionTokens = reader.IsDBNull(11) ? null : reader.GetInt32(11),
            ElapsedMs = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            Modality = (MessageModality)reader.GetInt32(13)
        };
    }
}
