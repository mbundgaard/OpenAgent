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

    public SqliteConversationStore(AgentEnvironment environment, ILogger<SqliteConversationStore> logger)
    {
        _logger = logger;
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
                VoiceSessionOpen INTEGER NOT NULL DEFAULT 0
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

        _logger.LogInformation("SQLite conversation store initialized at {ConnectionString}", _connectionString);
    }

    public Conversation GetOrCreate(string conversationId, string source, ConversationType type)
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
            CreatedAt = DateTimeOffset.UtcNow
        };

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Conversations (Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen)
            VALUES (@id, @source, @type, @createdAt, @voiceSessionId, @voiceSessionOpen)
            """;
        cmd.Parameters.AddWithValue("@id", conversation.Id);
        cmd.Parameters.AddWithValue("@source", conversation.Source);
        cmd.Parameters.AddWithValue("@type", (int)conversation.Type);
        cmd.Parameters.AddWithValue("@createdAt", conversation.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@voiceSessionId", (object?)conversation.VoiceSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@voiceSessionOpen", conversation.VoiceSessionOpen ? 1 : 0);
        cmd.ExecuteNonQuery();

        // Re-read in case of a race (INSERT OR IGNORE means another thread may have created it)
        return Get(conversationId) ?? conversation;
    }

    public IReadOnlyList<Conversation> GetAll()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen FROM Conversations ORDER BY CreatedAt DESC";

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
        cmd.CommandText = "SELECT Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen FROM Conversations WHERE Id = @id";
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
            SET Source = @source, Type = @type, VoiceSessionId = @voiceSessionId, VoiceSessionOpen = @voiceSessionOpen
            WHERE Id = @id
            """;
        cmd.Parameters.AddWithValue("@id", conversation.Id);
        cmd.Parameters.AddWithValue("@source", conversation.Source);
        cmd.Parameters.AddWithValue("@type", (int)conversation.Type);
        cmd.Parameters.AddWithValue("@voiceSessionId", (object?)conversation.VoiceSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@voiceSessionOpen", conversation.VoiceSessionOpen ? 1 : 0);
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
            INSERT INTO Messages (Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId)
            VALUES (@id, @conversationId, @role, @content, @createdAt, @toolCalls, @toolCallId, @channelMessageId, @replyToChannelMessageId)
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
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId FROM Messages WHERE ConversationId = @id ORDER BY CreatedAt";
        cmd.Parameters.AddWithValue("@id", conversationId);

        using var reader = cmd.ExecuteReader();
        var list = new List<Message>();
        while (reader.Read())
        {
            list.Add(new Message
            {
                Id = reader.GetString(0),
                ConversationId = reader.GetString(1),
                Role = reader.GetString(2),
                Content = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                ToolCalls = reader.IsDBNull(5) ? null : reader.GetString(5),
                ToolCallId = reader.IsDBNull(6) ? null : reader.GetString(6),
                ChannelMessageId = reader.IsDBNull(7) ? null : reader.GetString(7),
                ReplyToChannelMessageId = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return list;
    }

    public void Dispose()
    {
        // No persistent connection to dispose — each operation opens/closes its own
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
            VoiceSessionOpen = reader.GetInt32(5) != 0
        };
    }
}
