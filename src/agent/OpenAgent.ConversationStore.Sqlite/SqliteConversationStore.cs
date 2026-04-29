using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OpenAgent.Compaction;
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
    private readonly string _dataPath;
    private readonly CompactionConfig _compactionConfig;
    private readonly ICompactionSummarizer? _compactionSummarizer;

    /// <summary>
    /// Per-conversation linked cancellation tokens for in-flight compactions. Used to
    /// propagate cancellation when a conversation is deleted or the host shuts down.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _compactionCts = new();

    /// <summary>
    /// Tracks fire-and-forget background compaction tasks started by <see cref="TryStartCompaction"/>.
    /// <see cref="Dispose"/> waits on these so the host doesn't tear down while a background task
    /// is still using the logger / ICompactionSummarizer / DI container — a known cause of
    /// ObjectDisposedException flakes in CI test cleanup.
    /// </summary>
    private readonly ConcurrentDictionary<Task, byte> _backgroundCompactions = new();

    /// <summary>
    /// Set once <see cref="Dispose"/> has been called. Background compactions check this before
    /// they start work so we don't hand them resources we're about to dispose.
    /// </summary>
    private volatile bool _disposed;

    public SqliteConversationStore(
        AgentEnvironment environment,
        ILogger<SqliteConversationStore> logger,
        CompactionConfig compactionConfig,
        ICompactionSummarizer? compactionSummarizer = null)
    {
        _logger = logger;
        _compactionConfig = compactionConfig;
        _compactionSummarizer = compactionSummarizer;
        _dataPath = environment.DataPath;
        var dbPath = Path.Combine(_dataPath, "conversations.db");
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
        TryAddColumn(connection, "Conversations", "DisplayName", "TEXT");
        TryAddColumn(connection, "Conversations", "Intention", "TEXT");
        TryAddColumn(connection, "Messages", "ToolResultRef", "TEXT");
        TryAddColumn(connection, "Conversations", "ContextWindowTokens", "INTEGER");
        TryAddColumn(connection, "Conversations", "MentionFilter", "TEXT");

        // Split legacy Provider/Model into per-modality pairs (Apr 2026). The new columns
        // start blank for existing rows; the UPDATE below copies the legacy values into both
        // pairs so behavior is preserved until the user reassigns. Legacy columns stay in
        // the schema (SQLite can't drop columns cleanly) but are no longer read or written.
        TryAddColumn(connection, "Conversations", "TextProvider", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(connection, "Conversations", "TextModel", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(connection, "Conversations", "VoiceProvider", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(connection, "Conversations", "VoiceModel", "TEXT NOT NULL DEFAULT ''");

        // One-shot backfill from legacy Provider/Model where the new pair is still blank.
        // Idempotent — once columns are populated, this UPDATE matches zero rows.
        using (var backfill = connection.CreateCommand())
        {
            backfill.CommandText = """
                UPDATE Conversations
                SET TextProvider = Provider, TextModel = Model,
                    VoiceProvider = Provider, VoiceModel = Model
                WHERE TextProvider = '' AND TextModel = '' AND VoiceProvider = '' AND VoiceModel = ''
                  AND (Provider != '' OR Model != '')
                """;
            backfill.ExecuteNonQuery();
        }

        _logger.LogInformation("SQLite conversation store initialized at {ConnectionString}", _connectionString);
    }

    public Conversation GetOrCreate(
        string conversationId,
        string source,
        string textProvider,
        string textModel,
        string voiceProvider,
        string voiceModel)
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
            TextProvider = textProvider,
            TextModel = textModel,
            VoiceProvider = voiceProvider,
            VoiceModel = voiceModel,
            CreatedAt = DateTimeOffset.UtcNow
        };

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // Legacy columns Type, Provider, Model still exist on the schema. Insert empty/zero
        // values to satisfy NOT NULL constraints on old DBs; the live read path uses
        // TextProvider/TextModel/VoiceProvider/VoiceModel exclusively.
        cmd.CommandText = """
            INSERT OR IGNORE INTO Conversations (Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, Provider, Model, TextProvider, TextModel, VoiceProvider, VoiceModel, ActiveSkills, ChannelType, ConnectionId, ChannelChatId, MentionFilter)
            VALUES (@id, @source, 0, @createdAt, @voiceSessionId, @voiceSessionOpen, '', '', @textProvider, @textModel, @voiceProvider, @voiceModel, @activeSkills, @channelType, @connectionId, @channelChatId, @mentionFilter)
            """;
        cmd.Parameters.AddWithValue("@id", conversation.Id);
        cmd.Parameters.AddWithValue("@source", conversation.Source);
        cmd.Parameters.AddWithValue("@createdAt", conversation.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@voiceSessionId", (object?)conversation.VoiceSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@voiceSessionOpen", conversation.VoiceSessionOpen ? 1 : 0);
        cmd.Parameters.AddWithValue("@textProvider", conversation.TextProvider);
        cmd.Parameters.AddWithValue("@textModel", conversation.TextModel);
        cmd.Parameters.AddWithValue("@voiceProvider", conversation.VoiceProvider);
        cmd.Parameters.AddWithValue("@voiceModel", conversation.VoiceModel);
        cmd.Parameters.AddWithValue("@activeSkills",
            conversation.ActiveSkills is not null
                ? (object)JsonSerializer.Serialize(conversation.ActiveSkills)
                : DBNull.Value);
        cmd.Parameters.AddWithValue("@channelType", (object?)conversation.ChannelType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@connectionId", (object?)conversation.ConnectionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@channelChatId", (object?)conversation.ChannelChatId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mentionFilter",
            conversation.MentionFilter is { Count: > 0 }
                ? (object)JsonSerializer.Serialize(conversation.MentionFilter)
                : DBNull.Value);
        cmd.ExecuteNonQuery();

        // Re-read in case of a race (INSERT OR IGNORE means another thread may have created it)
        return Get(conversationId) ?? conversation;
    }

    public Conversation? FindChannelConversation(string channelType, string connectionId, string channelChatId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Source, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning, TextProvider, TextModel, VoiceProvider, VoiceModel, TotalPromptTokens, TotalCompletionTokens, TurnCount, LastActivity, ActiveSkills, ChannelType, ConnectionId, ChannelChatId, DisplayName, Intention, ContextWindowTokens, MentionFilter FROM Conversations
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
        string textProvider,
        string textModel,
        string voiceProvider,
        string voiceModel)
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
            TextProvider = textProvider,
            TextModel = textModel,
            VoiceProvider = voiceProvider,
            VoiceModel = voiceModel,
            ChannelType = channelType,
            ConnectionId = connectionId,
            ChannelChatId = channelChatId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        using (var connection = Open())
        using (var cmd = connection.CreateCommand())
        {
            // Legacy Type/Provider/Model columns kept for back-compat — see GetOrCreate.
            cmd.CommandText = """
                INSERT INTO Conversations (Id, Source, Type, CreatedAt, VoiceSessionId, VoiceSessionOpen, Provider, Model, TextProvider, TextModel, VoiceProvider, VoiceModel, ActiveSkills, ChannelType, ConnectionId, ChannelChatId, MentionFilter)
                VALUES (@id, @source, 0, @createdAt, @voiceSessionId, @voiceSessionOpen, '', '', @textProvider, @textModel, @voiceProvider, @voiceModel, @activeSkills, @channelType, @connectionId, @channelChatId, @mentionFilter)
                """;
            cmd.Parameters.AddWithValue("@id", conversation.Id);
            cmd.Parameters.AddWithValue("@source", conversation.Source);
            cmd.Parameters.AddWithValue("@createdAt", conversation.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@voiceSessionId", DBNull.Value);
            cmd.Parameters.AddWithValue("@voiceSessionOpen", 0);
            cmd.Parameters.AddWithValue("@textProvider", conversation.TextProvider);
            cmd.Parameters.AddWithValue("@textModel", conversation.TextModel);
            cmd.Parameters.AddWithValue("@voiceProvider", conversation.VoiceProvider);
            cmd.Parameters.AddWithValue("@voiceModel", conversation.VoiceModel);
            cmd.Parameters.AddWithValue("@activeSkills", DBNull.Value);
            cmd.Parameters.AddWithValue("@channelType", channelType);
            cmd.Parameters.AddWithValue("@connectionId", connectionId);
            cmd.Parameters.AddWithValue("@channelChatId", channelChatId);
            cmd.Parameters.AddWithValue("@mentionFilter",
                conversation.MentionFilter is { Count: > 0 }
                    ? (object)JsonSerializer.Serialize(conversation.MentionFilter)
                    : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        return conversation;
    }

    public IReadOnlyList<Conversation> GetAll()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Source, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning, TextProvider, TextModel, VoiceProvider, VoiceModel, TotalPromptTokens, TotalCompletionTokens, TurnCount, LastActivity, ActiveSkills, ChannelType, ConnectionId, ChannelChatId, DisplayName, Intention, ContextWindowTokens, MentionFilter FROM Conversations ORDER BY COALESCE(LastActivity, CreatedAt) DESC";

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
        cmd.CommandText = "SELECT Id, Source, CreatedAt, VoiceSessionId, VoiceSessionOpen, LastPromptTokens, Context, CompactedUpToRowId, CompactionRunning, TextProvider, TextModel, VoiceProvider, VoiceModel, TotalPromptTokens, TotalCompletionTokens, TurnCount, LastActivity, ActiveSkills, ChannelType, ConnectionId, ChannelChatId, DisplayName, Intention, ContextWindowTokens, MentionFilter FROM Conversations WHERE Id = @id";
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
            SET Source = @source, VoiceSessionId = @voiceSessionId,
                VoiceSessionOpen = @voiceSessionOpen, LastPromptTokens = @lastPromptTokens,
                Context = @context, CompactedUpToRowId = @compactedUpToRowId,
                CompactionRunning = @compactionRunning,
                TextProvider = @textProvider, TextModel = @textModel,
                VoiceProvider = @voiceProvider, VoiceModel = @voiceModel,
                TotalPromptTokens = @totalPromptTokens, TotalCompletionTokens = @totalCompletionTokens,
                TurnCount = @turnCount, LastActivity = @lastActivity, ActiveSkills = @activeSkills,
                ChannelType = @channelType, ConnectionId = @connectionId, ChannelChatId = @channelChatId,
                Intention = @intention, ContextWindowTokens = @contextWindowTokens,
                MentionFilter = @mentionFilter
            WHERE Id = @id
            """;
        cmd.Parameters.AddWithValue("@id", conversation.Id);
        cmd.Parameters.AddWithValue("@source", conversation.Source);
        cmd.Parameters.AddWithValue("@voiceSessionId", (object?)conversation.VoiceSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@voiceSessionOpen", conversation.VoiceSessionOpen ? 1 : 0);
        cmd.Parameters.AddWithValue("@lastPromptTokens", (object?)conversation.LastPromptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@context", (object?)conversation.Context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@compactedUpToRowId", (object?)conversation.CompactedUpToRowId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@compactionRunning", conversation.CompactionRunning ? 1 : 0);
        cmd.Parameters.AddWithValue("@textProvider", conversation.TextProvider);
        cmd.Parameters.AddWithValue("@textModel", conversation.TextModel);
        cmd.Parameters.AddWithValue("@voiceProvider", conversation.VoiceProvider);
        cmd.Parameters.AddWithValue("@voiceModel", conversation.VoiceModel);
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
        cmd.Parameters.AddWithValue("@intention", (object?)conversation.Intention ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@contextWindowTokens", (object?)conversation.ContextWindowTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mentionFilter",
            conversation.MentionFilter is { Count: > 0 }
                ? (object)JsonSerializer.Serialize(conversation.MentionFilter)
                : DBNull.Value);
        cmd.ExecuteNonQuery();

        TryStartCompaction(conversation);
    }

    public void SetVoiceSession(string conversationId, string? sessionId, bool open)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Conversations
            SET VoiceSessionId = @voiceSessionId,
                VoiceSessionOpen = @voiceSessionOpen
            WHERE Id = @id
            """;
        cmd.Parameters.AddWithValue("@id", conversationId);
        cmd.Parameters.AddWithValue("@voiceSessionId", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@voiceSessionOpen", open ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public bool Delete(string conversationId)
    {
        // Cancel any in-flight compaction before tearing down the conversation's data.
        // The compaction task's finally block will handle the disposal and UpdateCompactionState,
        // but since we're about to delete the conversation row those writes will be no-ops.
        if (_compactionCts.TryRemove(conversationId, out var cts))
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }

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
        var deleted = cmd.ExecuteNonQuery() > 0;

        // Remove on-disk tool result blobs for this conversation
        DeleteConversationBlobs(conversationId);

        return deleted;
    }

    public void AddMessage(string conversationId, Message message)
    {
        // If the caller provided full tool result content, persist it to disk first and
        // record the relative path in ToolResultRef. Content keeps the compact summary.
        string? toolResultRef = null;
        if (!string.IsNullOrEmpty(message.FullToolResult))
        {
            toolResultRef = SaveToolResultBlob(conversationId, message.Id, message.FullToolResult);
        }

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Messages (Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality, ToolResultRef)
            VALUES (@id, @conversationId, @role, @content, @createdAt, @toolCalls, @toolCallId, @channelMessageId, @replyToChannelMessageId, @promptTokens, @completionTokens, @elapsedMs, @modality, @toolResultRef)
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
        cmd.Parameters.AddWithValue("@toolResultRef", (object?)toolResultRef ?? DBNull.Value);
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

    public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false)
    {
        // UI-facing and provider-facing reads both get ONLY post-cut messages here.
        // The compaction summary lives on Conversation.Context; providers read it directly
        // and inject it as a <summary>-wrapped user message in their BuildChatMessages pass.
        var conversation = Get(conversationId);
        var list = ReadMessagesFromDb(conversationId, conversation?.CompactedUpToRowId);

        if (includeToolResultBlobs)
            LoadToolResultBlobs(conversationId, list);

        return list;
    }

    public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds, bool includeToolResultBlobs = false)
    {
        if (messageIds.Count == 0) return [];

        using var connection = Open();
        using var cmd = connection.CreateCommand();

        var paramNames = messageIds.Select((_, i) => $"@id{i}").ToList();
        cmd.CommandText = $"SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality, ToolResultRef FROM Messages WHERE Id IN ({string.Join(", ", paramNames)}) ORDER BY rowid";

        for (var i = 0; i < messageIds.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], messageIds[i]);

        using var reader = cmd.ExecuteReader();
        var list = new List<Message>();
        while (reader.Read())
        {
            list.Add(ReadMessage(reader));
        }

        if (includeToolResultBlobs)
        {
            // Group by conversationId — each blob lives under its conversation's directory
            var groups = list
                .Select((msg, idx) => (msg, idx))
                .Where(t => t.msg.Role == "tool" && t.msg.ToolResultRef is not null)
                .GroupBy(t => t.msg.ConversationId);

            foreach (var group in groups)
            {
                foreach (var (msg, idx) in group)
                {
                    var full = ReadToolResultBlob(group.Key, msg.ToolResultRef!);
                    if (full is null) continue;
                    list[idx] = CopyWithFullToolResult(msg, full);
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Walks the given list of messages and, for any tool messages with a ToolResultRef,
    /// reads the on-disk blob and returns a new Message copy with FullToolResult populated.
    /// Missing files are tolerated (logged as warnings in ReadToolResultBlob).
    /// </summary>
    private void LoadToolResultBlobs(string conversationId, List<Message> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role != "tool" || msg.ToolResultRef is null) continue;

            var full = ReadToolResultBlob(conversationId, msg.ToolResultRef);
            if (full is null) continue;

            messages[i] = CopyWithFullToolResult(msg, full);
        }
    }

    /// <summary>
    /// Returns a new Message with all fields copied from <paramref name="source"/>, plus
    /// <see cref="Message.FullToolResult"/> set to <paramref name="fullToolResult"/>.
    /// Message uses init-only properties so this explicit copy is how we "update" it.
    /// </summary>
    private static Message CopyWithFullToolResult(Message source, string fullToolResult)
    {
        return new Message
        {
            Id = source.Id,
            ConversationId = source.ConversationId,
            Role = source.Role,
            Content = source.Content,
            CreatedAt = source.CreatedAt,
            Modality = source.Modality,
            ToolCalls = source.ToolCalls,
            ToolCallId = source.ToolCallId,
            ChannelMessageId = source.ChannelMessageId,
            ReplyToChannelMessageId = source.ReplyToChannelMessageId,
            RowId = source.RowId,
            PromptTokens = source.PromptTokens,
            CompletionTokens = source.CompletionTokens,
            ElapsedMs = source.ElapsedMs,
            ToolResultRef = source.ToolResultRef,
            FullToolResult = fullToolResult
        };
    }

    public Task<bool> CompactNowAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default)
        => PerformCompactionAsync(conversationId, reason, customInstructions, ct);

    public void Dispose()
    {
        // Mark disposed first so any compaction Task.Run that hasn't started yet bails out
        // before touching resources we're about to tear down.
        _disposed = true;

        // Cancel any in-flight compactions so their summarizer awaits unblock promptly.
        // We deliberately do NOT Dispose the CTS here — the background task's finally
        // block holds a reference to linkedCts.Token, and disposing while it's still in
        // flight risks ObjectDisposedException on token registrations. The background
        // finally clears the dictionary entry and disposes the CTS itself; we'll wait
        // for those tasks below.
        foreach (var entry in _compactionCts)
        {
            try { entry.Value.Cancel(); } catch { /* ignore — already disposed by background */ }
        }

        // Wait for all background compaction tasks to finish before we let the host tear
        // down the DI container. Without this, a fire-and-forget Task.Run from
        // TryStartCompaction can still be running when the logger / summarizer / hosted
        // services get disposed, producing ObjectDisposedException at xUnit class teardown.
        // Bound the wait so a stuck task can't hang the host's 5-second StopAsync budget.
        var pending = _backgroundCompactions.Keys.ToArray();
        if (pending.Length > 0)
        {
            try { Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(2)); }
            catch { /* individual task exceptions are already logged in PerformCompactionAsync */ }
        }

        _compactionCts.Clear();
        _backgroundCompactions.Clear();
        // No persistent connection to dispose — each operation opens/closes its own.
    }

    /// <summary>
    /// Checks if compaction should run and starts it in the background if so.
    /// Called from Update() when LastPromptTokens is set.
    /// </summary>
    private void TryStartCompaction(Conversation conversation)
    {
        if (_disposed) return;
        if (_compactionSummarizer is null) return;
        if (conversation.CompactionRunning) return;
        if (conversation.LastPromptTokens is null) return;

        // Drive the threshold from the per-conversation context window (populated by the
        // provider on first turn). Falls back to CompactionConfig.MaxContextTokens when the
        // provider couldn't determine a window (unknown model, misconfiguration).
        var window = conversation.ContextWindowTokens ?? _compactionConfig.MaxContextTokens;
        var triggerThreshold = window * _compactionConfig.CompactionTriggerPercent / 100;
        if (conversation.LastPromptTokens.Value < triggerThreshold) return;

        // Background fire-and-forget — PerformCompactionAsync acquires the lock itself and
        // clears it on success/failure. No pre-locking here (doing so would race with the
        // guard inside PerformCompactionAsync, which reads the conversation fresh).
        // We track the Task so Dispose() can wait for it; without that, a stale background
        // run can outlive the DI container and throw ObjectDisposedException during
        // xUnit's IClassFixture teardown.
        Task? runTask = null;
        runTask = Task.Run(async () =>
        {
            try
            {
                if (_disposed) return;
                await PerformCompactionAsync(conversation.Id, CompactionReason.Threshold, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    _logger.LogError(ex, "Threshold compaction failed for conversation {ConversationId}", conversation.Id);
            }
            finally
            {
                if (runTask is not null)
                    _backgroundCompactions.TryRemove(runTask, out _);
            }
        });
        _backgroundCompactions.TryAdd(runTask, 0);
    }

    /// <summary>
    /// Core compaction path. Serves Threshold (background), Overflow (provider-sync), and
    /// Manual (endpoint) triggers. Returns true if a cut was made, false if there was
    /// nothing to compact or if compaction is disabled. Throws on summarizer errors —
    /// caller decides whether to surface, retry, or swallow.
    /// </summary>
    private async Task<bool> PerformCompactionAsync(
        string conversationId,
        CompactionReason reason,
        string? customInstructions,
        CancellationToken ct)
    {
        if (_compactionSummarizer is null) return false;

        var conversation = Get(conversationId);
        if (conversation is null) return false;
        if (conversation.CompactionRunning) return false;

        // Register a linked cancellation token scoped to this conversation. Delete/Dispose
        // trigger it; if another run is already in-flight, we bail rather than queue.
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_compactionCts.TryAdd(conversationId, linkedCts))
        {
            linkedCts.Dispose();
            return false;
        }

        // Acquire the running lock
        UpdateCompactionState(conversationId, compactionRunning: true, context: null, compactedUpToRowId: null);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation(
            "{@Event}",
            new { @event = "compaction.start", conversationId, reason = reason.ToString() });

        try
        {
            // Rebind ct inside the try so callees use the linked token
            ct = linkedCts.Token;
            // Load full tool result blobs so the summarizer sees real content, not stubs.
            var liveMessages = ReadMessagesFromDb(conversationId, conversation.CompactedUpToRowId);
            LoadToolResultBlobs(conversationId, liveMessages);

            // Snapshot the max rowid seen at this moment. Any message inserted by a concurrent
            // turn after this point must NOT be absorbed into the compaction cutoff. The cut
            // algorithm only considers entries from this snapshot, so this is an invariant
            // rather than a runtime check — but make it explicit to catch future regressions.
            var maxRowIdAtSnapshot = liveMessages.Count > 0
                ? liveMessages[^1].RowId
                : conversation.CompactedUpToRowId ?? 0;

            var cutIndex = CompactionCutPoint.Find(liveMessages, _compactionConfig.KeepRecentTokens);
            if (cutIndex is null or 0)
            {
                _logger.LogDebug("Nothing to compact for conversation {ConversationId} (reason: {Reason})",
                    conversationId, reason);
                return false;
            }

            var toCompact = liveMessages.GetRange(0, cutIndex.Value);
            var newCutoffRowId = toCompact[^1].RowId;

            // Invariant: the cutoff must be ≤ the snapshot max. Holds by construction because
            // toCompact is a prefix of liveMessages, but assert to guard against regressions
            // in the cut-point algorithm.
            if (newCutoffRowId > maxRowIdAtSnapshot)
                throw new InvalidOperationException(
                    $"Compaction cutoff {newCutoffRowId} exceeds snapshot {maxRowIdAtSnapshot} — logic error.");

            CompactionResult result;
            try
            {
                result = await _compactionSummarizer.SummarizeAsync(
                    conversation.Context, toCompact, customInstructions, ct);
            }
            catch (CompactionDisabledException)
            {
                // Summarizer already logged once; we just skip quietly so repeated triggers
                // don't produce a log storm.
                return false;
            }

            UpdateCompactionState(conversationId,
                compactionRunning: false,
                context: result.Context,
                compactedUpToRowId: newCutoffRowId);

            sw.Stop();
            _logger.LogInformation(
                "{@Event}",
                new
                {
                    @event = "compaction.complete",
                    conversationId,
                    reason = reason.ToString(),
                    messagesCompacted = toCompact.Count,
                    tokensBefore = conversation.LastPromptTokens,
                    summaryChars = result.Context.Length,
                    durationMs = sw.ElapsedMilliseconds
                });

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "{@Event}",
                new { @event = "compaction.cancelled", conversationId, reason = reason.ToString() });
            return false;
        }
        catch (Exception ex) when (ex is not CompactionDisabledException)
        {
            _logger.LogError(ex,
                "{@Event}",
                new { @event = "compaction.error", conversationId, reason = reason.ToString(), error = ex.Message });
            throw;
        }
        finally
        {
            // If an exception bailed us out before the success-path state swap, clear the lock.
            // The success path already cleared it via UpdateCompactionState above.
            var fresh = Get(conversationId);
            if (fresh?.CompactionRunning == true)
                UpdateCompactionState(conversationId, compactionRunning: false, context: null, compactedUpToRowId: null);

            // Always drop the CTS registration — whether we succeeded, cancelled, or threw.
            if (_compactionCts.TryRemove(conversationId, out var removed))
                removed.Dispose();
        }
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
            cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality, ToolResultRef FROM Messages WHERE ConversationId = @id AND rowid > @cutoff ORDER BY rowid";
            cmd.Parameters.AddWithValue("@cutoff", afterRowId.Value);
        }
        else
        {
            cmd.CommandText = "SELECT rowid, Id, ConversationId, Role, Content, CreatedAt, ToolCalls, ToolCallId, ChannelMessageId, ReplyToChannelMessageId, PromptTokens, CompletionTokens, ElapsedMs, Modality, ToolResultRef FROM Messages WHERE ConversationId = @id ORDER BY rowid";
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
            CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
            VoiceSessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
            VoiceSessionOpen = reader.GetInt32(4) != 0,
            LastPromptTokens = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Context = reader.IsDBNull(6) ? null : reader.GetString(6),
            CompactedUpToRowId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            CompactionRunning = !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
            TextProvider = reader.GetString(9),
            TextModel = reader.GetString(10),
            VoiceProvider = reader.GetString(11),
            VoiceModel = reader.GetString(12),
            TotalPromptTokens = reader.GetInt64(13),
            TotalCompletionTokens = reader.GetInt64(14),
            TurnCount = reader.GetInt32(15),
            LastActivity = reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)),
            ActiveSkills = reader.IsDBNull(17) ? null : JsonSerializer.Deserialize<List<string>>(reader.GetString(17)),
            ChannelType = reader.IsDBNull(18) ? null : reader.GetString(18),
            ConnectionId = reader.IsDBNull(19) ? null : reader.GetString(19),
            ChannelChatId = reader.IsDBNull(20) ? null : reader.GetString(20),
            DisplayName = reader.IsDBNull(21) ? null : reader.GetString(21),
            Intention = reader.IsDBNull(22) ? null : reader.GetString(22),
            ContextWindowTokens = reader.IsDBNull(23) ? null : reader.GetInt32(23),
            MentionFilter = reader.IsDBNull(24) ? null : JsonSerializer.Deserialize<List<string>>(reader.GetString(24))
        };
    }

    /// <summary>Updates the human-readable display name. No-op when nothing would change.</summary>
    public void UpdateDisplayName(string conversationId, string? displayName)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Conversations SET DisplayName = @displayName WHERE Id = @id AND IFNULL(DisplayName, '') != IFNULL(@displayName, '')";
        cmd.Parameters.AddWithValue("@id", conversationId);
        cmd.Parameters.AddWithValue("@displayName", (object?)displayName ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the absolute path to the tool-results directory for a conversation.
    /// Creates the directory if it does not exist.
    /// </summary>
    private string GetToolResultsDir(string conversationId)
    {
        var dir = Path.Combine(_dataPath, "conversations", conversationId, "tool-results");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Writes a tool result to disk atomically and returns the relative path
    /// (e.g. "tool-results/abc123.txt") for storage in ToolResultRef.
    /// Uses UTF-8 no BOM, matching FileSystemToolHandler.
    /// </summary>
    private string SaveToolResultBlob(string conversationId, string messageId, string content)
    {
        var dir = GetToolResultsDir(conversationId);
        var finalPath = Path.Combine(dir, $"{messageId}.txt");
        var tempPath = $"{finalPath}.tmp";

        File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, finalPath, overwrite: true);

        return $"tool-results/{messageId}.txt";
    }

    /// <summary>
    /// Reads a tool result from disk. Returns null if the file is missing (logged as a warning) —
    /// callers fall back to the compact summary in Message.Content.
    /// </summary>
    private string? ReadToolResultBlob(string conversationId, string relativePath)
    {
        var absolutePath = Path.Combine(_dataPath, "conversations", conversationId, relativePath);
        if (!File.Exists(absolutePath))
        {
            _logger.LogWarning("Tool result blob missing: {Path}", absolutePath);
            return null;
        }

        try
        {
            return File.ReadAllText(absolutePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read tool result blob: {Path}", absolutePath);
            return null;
        }
    }

    /// <summary>
    /// Removes the entire conversations/{conversationId} directory, including all tool result blobs.
    /// Idempotent — no-op if the directory does not exist.
    /// </summary>
    private void DeleteConversationBlobs(string conversationId)
    {
        var dir = Path.Combine(_dataPath, "conversations", conversationId);
        if (!Directory.Exists(dir)) return;

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete conversation blob directory: {Path}", dir);
        }
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
            Modality = (MessageModality)reader.GetInt32(13),
            ToolResultRef = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }
}
