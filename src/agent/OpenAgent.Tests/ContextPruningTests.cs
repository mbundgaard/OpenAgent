using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.ContextPruning;
using OpenAgent.Contracts;
using OpenAgent.ConversationStore.Sqlite;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class ContextPruningTests : IDisposable
{
    private readonly string _dbDir;
    private readonly SqliteConversationStore _store;

    public ContextPruningTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"openagent-prune-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbDir);
        var env = new AgentEnvironment { DataPath = _dbDir };
        _store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance, new CompactionConfig());
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    private void AddRound(string convId, string toolName, string callId, DateTimeOffset createdAt, string result = "full result content")
    {
        _store.AddMessage(convId, new Message
        {
            Id = Guid.NewGuid().ToString(), ConversationId = convId, Role = "assistant",
            ToolCalls = JsonSerializer.Serialize(new[] { new { id = callId, type = "function", function = new { name = toolName, arguments = "{}" } } }),
            CreatedAt = createdAt
        });
        _store.AddMessage(convId, new Message
        {
            Id = Guid.NewGuid().ToString(), ConversationId = convId, Role = "tool",
            Content = result, ToolType = toolName, ToolCallId = callId,
            CreatedAt = createdAt
        });
    }

    [Fact]
    public void PurgeOldToolRounds_keeps_last_K_regardless_of_age()
    {
        _store.GetOrCreate("c1", "test", ConversationType.Text, "p", "m");

        // 12 rounds, all older than cutoff — but only 7 should be purged (keep last 5).
        var oldTime = DateTimeOffset.UtcNow.AddDays(-5);
        for (var i = 0; i < 12; i++)
            AddRound("c1", "file_read", $"call_{i}", oldTime);

        var (rounds, resultRows) = _store.PurgeOldToolRounds("c1", keepLast: 5, cutoff: DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(7, rounds);
        Assert.Equal(7, resultRows);

        var messages = _store.GetMessages("c1");
        var purgedAssistants = messages.Count(m => m.Role == "assistant" && m.ToolCallsPurgedAt is not null);
        var livedAssistants = messages.Count(m => m.Role == "assistant" && m.ToolCallsPurgedAt is null);
        Assert.Equal(7, purgedAssistants);
        Assert.Equal(5, livedAssistants);
    }

    [Fact]
    public void PurgeOldToolRounds_skips_rows_newer_than_cutoff()
    {
        _store.GetOrCreate("c1", "test", ConversationType.Text, "p", "m");

        // 10 old rounds + 3 recent rounds. Purge should only touch old rounds beyond K=5.
        var oldTime = DateTimeOffset.UtcNow.AddDays(-5);
        var recentTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 10; i++)
            AddRound("c1", "file_read", $"old_{i}", oldTime);
        for (var i = 0; i < 3; i++)
            AddRound("c1", "file_read", $"recent_{i}", recentTime);

        var (rounds, _) = _store.PurgeOldToolRounds("c1", keepLast: 5, cutoff: DateTimeOffset.UtcNow.AddHours(-24));

        // Last-K = 5 most recent (the 3 recent + 2 newest of the old). That leaves 8 old rounds, all past the cutoff → all 8 should be purged.
        Assert.Equal(8, rounds);
    }

    [Fact]
    public void PurgeOldToolRounds_handles_parallel_tool_calls_atomically()
    {
        _store.GetOrCreate("c1", "test", ConversationType.Text, "p", "m");

        var oldTime = DateTimeOffset.UtcNow.AddDays(-5);

        // Round with THREE parallel tool calls — one assistant row, three tool-result children.
        _store.AddMessage("c1", new Message
        {
            Id = "asst_1", ConversationId = "c1", Role = "assistant",
            ToolCalls = JsonSerializer.Serialize(new[]
            {
                new { id = "a", type = "function", function = new { name = "file_read", arguments = "{}" } },
                new { id = "b", type = "function", function = new { name = "web_fetch", arguments = "{}" } },
                new { id = "c", type = "function", function = new { name = "shell_exec", arguments = "{}" } }
            }),
            CreatedAt = oldTime
        });
        foreach (var id in new[] { "a", "b", "c" })
        {
            _store.AddMessage("c1", new Message
            {
                Id = $"tool_{id}", ConversationId = "c1", Role = "tool",
                Content = "result", ToolType = "x", ToolCallId = id,
                CreatedAt = oldTime
            });
        }

        var (rounds, resultRows) = _store.PurgeOldToolRounds("c1", keepLast: 0, cutoff: DateTimeOffset.UtcNow);

        Assert.Equal(1, rounds);
        Assert.Equal(3, resultRows); // all three children purged together
    }

    [Fact]
    public void PurgeSkillResourceResults_nulls_only_matching_rows()
    {
        _store.GetOrCreate("c1", "test", ConversationType.Text, "p", "m");

        _store.AddMessage("c1", new Message
        {
            Id = "t1", ConversationId = "c1", Role = "tool",
            Content = "file content", ToolType = "file_read", ToolCallId = "a"
        });
        _store.AddMessage("c1", new Message
        {
            Id = "t2", ConversationId = "c1", Role = "tool",
            Content = "skill resource content", ToolType = "activate_skill_resource", ToolCallId = "b"
        });
        _store.AddMessage("c1", new Message
        {
            Id = "t3", ConversationId = "c1", Role = "tool",
            Content = "another skill resource", ToolType = "activate_skill_resource", ToolCallId = "c"
        });

        var purged = _store.PurgeSkillResourceResults("c1");

        Assert.Equal(2, purged);

        var messages = _store.GetMessages("c1");
        Assert.Equal("file content", messages.First(m => m.Id == "t1").Content);
        Assert.Null(messages.First(m => m.Id == "t2").Content);
        Assert.Null(messages.First(m => m.Id == "t3").Content);
        Assert.NotNull(messages.First(m => m.Id == "t2").ToolResultPurgedAt);
    }

    [Fact]
    public void ContextPruneService_OnTurnPersisted_triggers_purge_when_over_threshold()
    {
        _store.GetOrCreate("c1", "test", ConversationType.Text, "p", "m");

        var oldTime = DateTimeOffset.UtcNow.AddDays(-5);
        for (var i = 0; i < 15; i++)
            AddRound("c1", "file_read", $"call_{i}", oldTime);

        var agentConfig = new AgentConfig { PurgeKeepLast = 3, PurgeAgeCutoffHours = 24, PurgeReactiveThresholdPercent = 50 };
        var compactionConfig = new CompactionConfig { MaxContextTokens = 1000 };

        var service = new ContextPruneService(
            new Lazy<IConversationStore>(() => _store),
            agentConfig, compactionConfig, NullLogger<ContextPruneService>.Instance);

        var conversation = _store.Get("c1")!;
        conversation.LastPromptTokens = 600; // 60% of 1000, over threshold
        service.OnTurnPersisted(conversation);

        var messages = _store.GetMessages("c1");
        var purged = messages.Count(m => m.Role == "assistant" && m.ToolCallsPurgedAt is not null);
        Assert.Equal(12, purged); // 15 rounds - 3 kept
    }

    [Fact]
    public void ContextPruneService_OnTurnPersisted_no_op_under_threshold()
    {
        _store.GetOrCreate("c1", "test", ConversationType.Text, "p", "m");

        var oldTime = DateTimeOffset.UtcNow.AddDays(-5);
        for (var i = 0; i < 15; i++)
            AddRound("c1", "file_read", $"call_{i}", oldTime);

        var agentConfig = new AgentConfig { PurgeKeepLast = 3, PurgeAgeCutoffHours = 24, PurgeReactiveThresholdPercent = 50 };
        var compactionConfig = new CompactionConfig { MaxContextTokens = 1000 };
        var service = new ContextPruneService(
            new Lazy<IConversationStore>(() => _store),
            agentConfig, compactionConfig, NullLogger<ContextPruneService>.Instance);

        var conversation = _store.Get("c1")!;
        conversation.LastPromptTokens = 400; // 40% — below 50% threshold
        service.OnTurnPersisted(conversation);

        var messages = _store.GetMessages("c1");
        Assert.DoesNotContain(messages, m => m.ToolCallsPurgedAt is not null);
    }

    [Fact]
    public void PurgeOldToolRounds_skips_already_purged_rows()
    {
        _store.GetOrCreate("c1", "test", ConversationType.Text, "p", "m");

        var oldTime = DateTimeOffset.UtcNow.AddDays(-5);
        for (var i = 0; i < 10; i++)
            AddRound("c1", "file_read", $"call_{i}", oldTime);

        // First run purges 5.
        var first = _store.PurgeOldToolRounds("c1", keepLast: 5, cutoff: DateTimeOffset.UtcNow.AddHours(-24));
        Assert.Equal(5, first.RoundsPurged);

        // Second run should be a no-op.
        var second = _store.PurgeOldToolRounds("c1", keepLast: 5, cutoff: DateTimeOffset.UtcNow.AddHours(-24));
        Assert.Equal(0, second.RoundsPurged);
        Assert.Equal(0, second.ResultRowsPurged);
    }
}
