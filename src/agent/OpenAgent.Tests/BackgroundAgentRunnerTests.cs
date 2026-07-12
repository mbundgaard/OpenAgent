using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.BackgroundAgent;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
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

        var runner = new BackgroundAgentRunner(
            store, factory, environment, config, jobStore,
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

    [Fact]
    public async Task RunAsync_creates_background_conversation_with_correct_source()
    {
        var (runner, store, _, _) = Build();
        await runner.RunAsync(CancellationToken.None);

        var conv = store.Get(BackgroundAgentRunner.BackgroundConversationId);
        Assert.NotNull(conv);
        Assert.Equal("background", conv!.Source);
    }

    [Fact]
    public async Task RunAsync_skips_when_text_provider_unset()
    {
        var (runner, store, config, _) = Build();
        config.TextProvider = "";
        await runner.RunAsync(CancellationToken.None);

        // No conversation created because we bailed early
        Assert.Null(store.Get(BackgroundAgentRunner.BackgroundConversationId));
    }

    [Fact]
    public async Task RunAsync_feeds_inbox_contents_to_provider()
    {
        var inboxPath = Path.Combine(_dataPath, "memory", "background", "INBOX.md");
        Directory.CreateDirectory(Path.GetDirectoryName(inboxPath)!);
        File.WriteAllText(inboxPath, "- https://example.com/article (summarised)");

        var captor = new CapturingTextProvider("ok");
        var (runner, _, _, _) = Build(provider: captor);
        await runner.RunAsync(CancellationToken.None);

        Assert.Contains("https://example.com/article", captor.LastUserContent);
    }

    [Fact]
    public async Task RunAsync_includes_sandbox_file_listing()
    {
        var sandboxFile = Path.Combine(_dataPath, "memory", "background", "active-threads.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sandboxFile)!);
        File.WriteAllText(sandboxFile, "researching sqlite-vec");

        var captor = new CapturingTextProvider("ok");
        var (runner, _, _, _) = Build(provider: captor);
        await runner.RunAsync(CancellationToken.None);

        Assert.Contains("active-threads.md", captor.LastUserContent);
        Assert.Contains("researching sqlite-vec", captor.LastUserContent);
    }
}
