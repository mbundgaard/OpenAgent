using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.BackgroundAgent;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class BackgroundAgentNudgeSweepTests
{
    private const string MainId = "main";

    [Fact]
    public void SweepOrphanedNudges_removes_an_orphaned_heartbeat_nudge()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");

        // Simulates a heartbeat that was hard-killed before its `finally` cleanup ran: the nudge
        // was persisted but never removed.
        store.AddMessage(MainId, new Message
        {
            Id = "orphan-1",
            ConversationId = MainId,
            Role = "user",
            Content = "[Heartbeat]\n\nReflect on the conversation above."
        });

        var config = new AgentConfig { MainConversationId = MainId };
        var sweep = new BackgroundAgentNudgeSweep(store, config, NullLogger<BackgroundAgentNudgeSweep>.Instance);

        sweep.SweepOrphanedNudges();

        Assert.Empty(store.GetMessages(MainId));
    }

    [Fact]
    public void SweepOrphanedNudges_leaves_real_user_messages_untouched()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        store.AddMessage(MainId, new Message
        {
            Id = "real-1",
            ConversationId = MainId,
            Role = "user",
            Content = "Did you take care of the invoice?"
        });
        store.AddMessage(MainId, new Message
        {
            Id = "orphan-1",
            ConversationId = MainId,
            Role = "user",
            Content = "[Heartbeat]\n\nReflect on the conversation above."
        });

        var config = new AgentConfig { MainConversationId = MainId };
        var sweep = new BackgroundAgentNudgeSweep(store, config, NullLogger<BackgroundAgentNudgeSweep>.Instance);

        sweep.SweepOrphanedNudges();

        var remaining = Assert.Single(store.GetMessages(MainId));
        Assert.Equal("real-1", remaining.Id);
    }

    [Fact]
    public void SweepOrphanedNudges_leaves_assistant_replies_untouched()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        // A normal heartbeat turn that spoke: the nudge was cleaned up (as designed), leaving
        // only the assistant reply. The sweep must not touch that reply just because it followed
        // a heartbeat.
        store.AddMessage(MainId, new Message
        {
            Id = "assistant-1",
            ConversationId = MainId,
            Role = "assistant",
            Content = "Monday's shot isn't logged - did you take it?"
        });

        var config = new AgentConfig { MainConversationId = MainId };
        var sweep = new BackgroundAgentNudgeSweep(store, config, NullLogger<BackgroundAgentNudgeSweep>.Instance);

        sweep.SweepOrphanedNudges();

        Assert.Single(store.GetMessages(MainId));
    }

    [Fact]
    public void SweepOrphanedNudges_no_ops_when_main_conversation_id_unset()
    {
        var store = new InMemoryConversationStore();
        var config = new AgentConfig { MainConversationId = null };
        var sweep = new BackgroundAgentNudgeSweep(store, config, NullLogger<BackgroundAgentNudgeSweep>.Instance);

        var exception = Record.Exception(() => sweep.SweepOrphanedNudges());

        Assert.Null(exception);
    }

    [Fact]
    public void SweepOrphanedNudges_no_ops_when_main_conversation_missing()
    {
        var store = new InMemoryConversationStore();
        var config = new AgentConfig { MainConversationId = "does-not-exist" };
        var sweep = new BackgroundAgentNudgeSweep(store, config, NullLogger<BackgroundAgentNudgeSweep>.Instance);

        var exception = Record.Exception(() => sweep.SweepOrphanedNudges());

        Assert.Null(exception);
    }
}
