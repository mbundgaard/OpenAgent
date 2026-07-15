using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.BackgroundAgent;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.ScheduledTasks;
using OpenAgent.ScheduledTasks.SystemJobs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class BackgroundAgentRunnerTests : IDisposable
{
    private const string MainId = "main";
    private readonly string _dataPath;

    public BackgroundAgentRunnerTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "openagent-bgrunner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best-effort */ }
    }

    private (BackgroundAgentRunner runner, InMemoryConversationStore store, AgentConfig config, SystemJobStateStore jobState)
        Build(string? mainId = MainId, ILlmTextProvider? provider = null)
    {
        var store = new InMemoryConversationStore();
        if (mainId is not null)
            store.GetOrCreate(mainId, "telegram", "p", "m", "vp", "vm");

        var config = new AgentConfig
        {
            BackgroundAgentEnabled = true,
            MainConversationId = mainId,
            TextProvider = "fake",
            TextModel = "m"
        };

        var jobStore = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        var environment = new AgentEnvironment { DataPath = _dataPath };
        Func<string, ILlmTextProvider> factory = _ => provider ?? new StreamingTextProvider("ok");

        var router = new DeliveryRouter(
            new NoopConnectionManager(),
            new NoopWebSocketRegistry(),
            NullLogger<DeliveryRouter>.Instance);

        var runner = new BackgroundAgentRunner(
            store, factory, environment, config, jobStore, router,
            NullLogger<BackgroundAgentRunner>.Instance);
        return (runner, store, config, jobStore);
    }

    [Fact]
    public async Task ShouldRun_false_when_master_switch_disabled()
    {
        var (runner, _, config, _) = Build();
        config.BackgroundAgentEnabled = false;
        Assert.False(await runner.ShouldRunAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ShouldRun_false_when_main_conversation_id_unset()
    {
        var (runner, _, _, _) = Build(mainId: null);
        Assert.False(await runner.ShouldRunAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ShouldRun_false_when_main_conversation_missing()
    {
        var (runner, store, _, _) = Build();
        store.Delete(MainId);
        Assert.False(await runner.ShouldRunAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ShouldRun_false_when_less_than_30min_since_last_run()
    {
        var (runner, _, _, jobState) = Build();
        var state = jobState.GetOrCreate(BackgroundAgentRunner.JobName);
        state.LastRunAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        Assert.False(await runner.ShouldRunAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ShouldRun_true_when_30min_elapsed_since_last_run_and_main_idle()
    {
        var (runner, _, _, jobState) = Build();
        var state = jobState.GetOrCreate(BackgroundAgentRunner.JobName);
        state.LastRunAt = DateTimeOffset.UtcNow.AddMinutes(-45);
        // Main conversation has no messages → quiet by default
        Assert.True(await runner.ShouldRunAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ShouldRun_false_when_user_messaged_main_less_than_15min_ago()
    {
        var (runner, store, _, _) = Build();
        store.AddMessage(MainId, new Message
        {
            Id = "m1", ConversationId = MainId, Role = "user", Content = "hi",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        Assert.False(await runner.ShouldRunAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ShouldRun_true_when_main_last_active_long_enough_ago()
    {
        var (runner, store, _, _) = Build();
        store.AddMessage(MainId, new Message
        {
            Id = "m1", ConversationId = MainId, Role = "user", Content = "old",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        });
        Assert.True(await runner.ShouldRunAsync(DateTimeOffset.UtcNow));
    }

    // The heartbeat runs IN the main conversation - that is the whole point of the redesign.
    // It must not create a conversation of its own.
    [Fact]
    public async Task RunAsync_runs_the_turn_in_the_main_conversation()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "Monday's shot is not logged - did you take it?");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        var reply = Assert.Single(store.GetMessages(MainId), m => m.Role == "assistant");
        Assert.Contains("Monday's shot", reply.Content);
    }

    // The nudge is scaffolding, not conversation. It must never touch the store - not even
    // transiently. Asserting it is gone AFTER RunAsync completes is not enough: the old
    // persist-then-delete design also passed that check, yet left a real window where a
    // concurrent turn on the same conversation (e.g. a real Telegram message arriving mid-
    // heartbeat) could read the nudge while it sat in the database. The mid-turn snapshot
    // recorded by PersistingTextProvider (taken before the turn yields anything back) proves
    // the store never contained the nudge at any point, which is the assertion that actually
    // closes the concurrency window.
    [Fact]
    public async Task RunAsync_never_writes_the_nudge_to_the_store_not_even_mid_turn()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "something worth saying");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        // After the turn: no user message left behind.
        Assert.Empty(store.GetMessages(MainId).Where(m => m.Role == "user"));

        // During the turn: the conversation was completely empty at the mid-turn snapshot point
        // (taken before the reply is yielded) — the strongest form of this assertion, since it
        // would fail against the old persist-then-delete implementation even though that
        // implementation passed the after-the-fact check above.
        Assert.Empty(provider.StoreContentsDuringTurn);

        Assert.Single(provider.PersistedUserContents); // the provider DID receive a nudge
        Assert.Contains("[Heartbeat]", provider.PersistedUserContents[0]);
    }

    // BuildNudge inlines BACKGROUND.md fresh from disk every run - this is the entire point of the
    // redesign (the old SystemPromptBuilder injection path is gone). Prove the provider actually
    // receives the instructions, not just that a nudge with the "[Heartbeat]" marker was sent.
    [Fact]
    public async Task RunAsync_inlines_BACKGROUND_md_content_into_the_nudge()
    {
        const string DistinctiveInstructions = "Check whether Martin logged his insulin shot today before nudging about anything else.";
        File.WriteAllText(Path.Combine(_dataPath, "BACKGROUND.md"), DistinctiveInstructions);

        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "something worth saying");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        Assert.Single(provider.PersistedUserContents);
        Assert.Contains(DistinctiveInstructions, provider.PersistedUserContents[0]);
    }

    // BuildNudge must tolerate a missing BACKGROUND.md - the heartbeat should still run and still
    // carry its own scaffolding (the marker and the "reply with []" instruction) even with nothing
    // to inline.
    [Fact]
    public async Task RunAsync_still_sends_a_valid_nudge_when_BACKGROUND_md_is_missing()
    {
        Assert.False(File.Exists(Path.Combine(_dataPath, "BACKGROUND.md")));

        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "something worth saying");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        Assert.Single(provider.PersistedUserContents);
        var nudgeContent = provider.PersistedUserContents[0];
        Assert.Contains("[Heartbeat]", nudgeContent);
        Assert.Contains("reply with exactly [] and nothing else.", nudgeContent);
    }

    // A silent run must leave the thread exactly as it found it.
    [Fact]
    public async Task RunAsync_silent_turn_leaves_main_conversation_untouched()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "nothing new here.\n\n[]");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        Assert.Empty(store.GetMessages(MainId));
    }

    // If the completion throws, the nudge must still be cleaned up - otherwise a crash
    // leaves "[Heartbeat]" sitting in the user's chat.
    [Fact]
    public async Task RunAsync_removes_the_nudge_even_when_the_provider_throws()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var (runner, _, _, _) = BuildWith(store, new ThrowingTextProvider());

        await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(CancellationToken.None));

        Assert.Empty(store.GetMessages(MainId).Where(m => m.Role == "user"));
    }

    [Fact]
    public async Task RunAsync_no_ops_when_main_conversation_id_unset()
    {
        var (runner, store, config, _) = Build();
        config.MainConversationId = null;

        await runner.RunAsync(CancellationToken.None);

        Assert.Empty(store.GetMessages(MainId));
    }

    // BLOCKER regression (final review). Round 1 narrates and calls a tool ("Updating today's
    // log."); round 2 is the "[]" sentinel. The provider persists nothing and deletes the whole
    // turn from history. Before the fix, the runner concatenated TextDelta across BOTH rounds
    // and re-derived suppression from that concatenation ("Updating today's log.[]" does not end
    // in a bare "[]" line, so IsSuppressed returned false) - delivering junk text to the user's
    // real channel for a turn the provider itself discarded. The fix consumes the
    // ResponseSuppressed event the provider already emits instead of re-deriving it.
    [Fact]
    public async Task RunAsync_delivers_nothing_when_the_final_round_after_a_tool_call_is_suppressed()
    {
        var store = new InMemoryConversationStore();
        BindMainConversationToChannel(store);
        var provider = new MultiRoundTextProvider(store, ["Updating today's log."], "[]");
        var (runner, sender) = BuildWithChannelDelivery(store, provider);

        await runner.RunAsync(CancellationToken.None);

        Assert.Empty(sender.SentMessages);
        Assert.Empty(store.GetMessages(MainId));
    }

    // Second-order bug from the same root cause: a heartbeat that legitimately speaks AFTER a
    // tool round. Before the fix, the runner delivered "round-1 narration + final answer" while
    // the provider persisted only the final answer to history - delivered text diverged from
    // stored history. The fix resets the buffer on every ToolCallEvent so only the final round's
    // text (which must equal what the provider persisted) is ever delivered.
    [Fact]
    public async Task RunAsync_delivers_only_the_final_rounds_text_after_a_tool_call()
    {
        var store = new InMemoryConversationStore();
        BindMainConversationToChannel(store);
        var provider = new MultiRoundTextProvider(store, ["Checking..."], "Monday's shot isn't logged.");
        var (runner, sender) = BuildWithChannelDelivery(store, provider);

        await runner.RunAsync(CancellationToken.None);

        var sent = Assert.Single(sender.SentMessages);
        Assert.Equal("Monday's shot isn't logged.", sent.Text);
        Assert.DoesNotContain("Checking...", sent.Text);
        // Delivered text must equal what the provider actually persisted as the assistant message.
        Assert.Equal(provider.PersistedAssistantContent, sent.Text);
    }

    private void BindMainConversationToChannel(InMemoryConversationStore store)
    {
        var conversation = store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        conversation.ChannelType = "telegram";
        conversation.ConnectionId = "conn-1";
        conversation.ChannelChatId = "chat-1";
        store.Update(conversation);
    }

    private (BackgroundAgentRunner runner, FakeOutboundChannelProvider sender)
        BuildWithChannelDelivery(InMemoryConversationStore store, ILlmTextProvider provider)
    {
        var config = new AgentConfig
        {
            BackgroundAgentEnabled = true,
            MainConversationId = MainId,
            TextProvider = "fake",
            TextModel = "m"
        };
        var jobStore = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        var environment = new AgentEnvironment { DataPath = _dataPath };
        var sender = new FakeOutboundChannelProvider();
        var router = new DeliveryRouter(sender, new NoopWebSocketRegistry(), NullLogger<DeliveryRouter>.Instance);

        var runner = new BackgroundAgentRunner(
            store, _ => provider, environment, config, jobStore, router,
            NullLogger<BackgroundAgentRunner>.Instance);
        return (runner, sender);
    }

    private (BackgroundAgentRunner runner, InMemoryConversationStore store, AgentConfig config, SystemJobStateStore jobState)
        BuildWith(InMemoryConversationStore store, ILlmTextProvider provider)
    {
        var config = new AgentConfig
        {
            BackgroundAgentEnabled = true,
            MainConversationId = MainId,
            TextProvider = "fake",
            TextModel = "m"
        };
        var jobStore = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        var environment = new AgentEnvironment { DataPath = _dataPath };
        var router = new DeliveryRouter(
            new NoopConnectionManager(),
            new NoopWebSocketRegistry(),
            NullLogger<DeliveryRouter>.Instance);

        var runner = new BackgroundAgentRunner(
            store, _ => provider, environment, config, jobStore, router,
            NullLogger<BackgroundAgentRunner>.Instance);
        return (runner, store, config, jobStore);
    }

    // AgentConfig.BackgroundAgentModel lets the heartbeat run on a cheaper model than the main
    // conversation's own TextModel (mirrors CompactionModel for the digest). When set, the runner
    // must forward it as CompleteAsync's modelOverride parameter.
    [Fact]
    public async Task RunAsync_passes_BackgroundAgentModel_as_modelOverride_when_set()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "something worth saying");

        var config = new AgentConfig
        {
            BackgroundAgentEnabled = true,
            MainConversationId = MainId,
            TextProvider = "fake",
            TextModel = "m",
            BackgroundAgentModel = "cheap-model"
        };
        var jobStore = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        var environment = new AgentEnvironment { DataPath = _dataPath };
        var router = new DeliveryRouter(
            new NoopConnectionManager(),
            new NoopWebSocketRegistry(),
            NullLogger<DeliveryRouter>.Instance);

        var runner = new BackgroundAgentRunner(
            store, _ => provider, environment, config, jobStore, router,
            NullLogger<BackgroundAgentRunner>.Instance);

        await runner.RunAsync(CancellationToken.None);

        Assert.Equal("cheap-model", provider.LastModelOverride);
    }

    // When BackgroundAgentModel is unset (the default), the runner must pass null so the provider
    // falls back to the main conversation's own TextModel — existing behavior for everyone who
    // hasn't opted into a separate heartbeat model.
    [Fact]
    public async Task RunAsync_passes_null_modelOverride_when_BackgroundAgentModel_is_unset()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "p", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "something worth saying");
        var (runner, _, _, _) = BuildWith(store, provider);

        await runner.RunAsync(CancellationToken.None);

        Assert.Null(provider.LastModelOverride);
    }

    // AgentConfig.BackgroundAgentProvider lets the heartbeat run on a different provider than the
    // main conversation's own TextProvider. When set, the runner must resolve THAT key via the
    // Func<string, ILlmTextProvider> resolver instead of conversation.TextProvider.
    [Fact]
    public async Task RunAsync_resolves_BackgroundAgentProvider_key_when_set()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "conversation-provider", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "something worth saying");

        var config = new AgentConfig
        {
            BackgroundAgentEnabled = true,
            MainConversationId = MainId,
            TextProvider = "fake",
            TextModel = "m",
            BackgroundAgentProvider = "cheap-provider"
        };
        var jobStore = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        var environment = new AgentEnvironment { DataPath = _dataPath };
        var router = new DeliveryRouter(
            new NoopConnectionManager(),
            new NoopWebSocketRegistry(),
            NullLogger<DeliveryRouter>.Instance);

        var requestedKeys = new List<string>();
        Func<string, ILlmTextProvider> resolver = key =>
        {
            requestedKeys.Add(key);
            return provider;
        };

        var runner = new BackgroundAgentRunner(
            store, resolver, environment, config, jobStore, router,
            NullLogger<BackgroundAgentRunner>.Instance);

        await runner.RunAsync(CancellationToken.None);

        Assert.Contains("cheap-provider", requestedKeys);
        Assert.DoesNotContain("conversation-provider", requestedKeys);
    }

    // When BackgroundAgentProvider is unset, the runner must keep resolving the main
    // conversation's own TextProvider — existing behavior.
    [Fact]
    public async Task RunAsync_resolves_conversation_TextProvider_key_when_BackgroundAgentProvider_is_unset()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate(MainId, "telegram", "conversation-provider", "m", "vp", "vm");
        var provider = new PersistingTextProvider(store, "something worth saying");

        var config = new AgentConfig
        {
            BackgroundAgentEnabled = true,
            MainConversationId = MainId,
            TextProvider = "fake",
            TextModel = "m"
        };
        var jobStore = new SystemJobStateStore(Path.Combine(_dataPath, "system-jobs.json"));
        var environment = new AgentEnvironment { DataPath = _dataPath };
        var router = new DeliveryRouter(
            new NoopConnectionManager(),
            new NoopWebSocketRegistry(),
            NullLogger<DeliveryRouter>.Instance);

        var requestedKeys = new List<string>();
        Func<string, ILlmTextProvider> resolver = key =>
        {
            requestedKeys.Add(key);
            return provider;
        };

        var runner = new BackgroundAgentRunner(
            store, resolver, environment, config, jobStore, router,
            NullLogger<BackgroundAgentRunner>.Instance);

        await runner.RunAsync(CancellationToken.None);

        Assert.Contains("conversation-provider", requestedKeys);
    }
}
