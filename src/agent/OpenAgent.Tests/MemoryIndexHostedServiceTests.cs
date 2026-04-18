using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.MemoryIndex;
using OpenAgent.Models.Configs;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class MemoryIndexHostedServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _memoryDir;
    private readonly MemoryChunkStore _store;
    private readonly AgentEnvironment _env;

    public MemoryIndexHostedServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openagent-memhost-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _memoryDir = Path.Combine(_tempDir, "memory");
        Directory.CreateDirectory(_memoryDir);
        _env = new AgentEnvironment { DataPath = _tempDir };
        _store = new MemoryChunkStore(Path.Combine(_tempDir, "memory.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // 01:00 UTC = 03:00 Copenhagen (CEST) on 2026-04-18 — past the default 02:00 run hour
    private static readonly DateTimeOffset ThreeAmCopenhagen = new(2026, 4, 18, 1, 0, 0, TimeSpan.Zero);
    // 23:00 UTC on 2026-04-17 = 01:00 Copenhagen on 2026-04-18 — before the 02:00 run hour
    private static readonly DateTimeOffset OneAmCopenhagen = new(2026, 4, 17, 23, 0, 0, TimeSpan.Zero);
    // Next day, same hour
    private static readonly DateTimeOffset ThreeAmCopenhagenNextDay = new(2026, 4, 19, 1, 0, 0, TimeSpan.Zero);

    private MemoryIndexService BuildService(AgentConfig config, string chunkerJson = """{"chunks":[{"content":"topic one","summary":"one"}]}""")
    {
        var provider = new StreamingTextProvider(chunkerJson);
        var chunker = new MemoryChunker(_ => provider, config);
        return new MemoryIndexService(
            _store,
            chunker,
            _ => new FakeEmbeddingProvider(),
            config,
            _env,
            NullLogger<MemoryIndexService>.Instance);
    }

    private MemoryIndexHostedService Build(AgentConfig config, Func<DateTimeOffset> clock, string? chunkerJson = null) =>
        new(
            chunkerJson is null ? BuildService(config) : BuildService(config, chunkerJson),
            _store,
            config,
            NullLogger<MemoryIndexHostedService>.Instance,
            clock);

    private void WritePastWindowFile(string date = "2026-04-10")
    {
        File.WriteAllText(Path.Combine(_memoryDir, $"{date}.md"), new string('x', 80));
    }

    private void FillMemoryWindow()
    {
        // MemoryIndexService takes "most recent N" as the window the same way SystemPromptBuilder
        // does, so to make an old file count as "past the window" there have to be N newer files.
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-18.md"), new string('a', 80));
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-17.md"), new string('b', 80));
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-16.md"), new string('c', 80));
    }

    [Fact]
    public async Task Skips_when_embedding_provider_is_empty()
    {
        var config = new AgentConfig { EmbeddingProvider = "", IndexRunAtHour = 2 };
        var host = Build(config, () => ThreeAmCopenhagen);

        await host.CheckAndRunAsync(CancellationToken.None);

        Assert.Null(_store.GetLastRunDate());
    }

    [Fact]
    public async Task Skips_when_local_hour_is_before_IndexRunAtHour()
    {
        var config = new AgentConfig { EmbeddingProvider = "fake", IndexRunAtHour = 2 };
        var host = Build(config, () => OneAmCopenhagen);

        await host.CheckAndRunAsync(CancellationToken.None);

        Assert.Null(_store.GetLastRunDate());
    }

    [Fact]
    public async Task Runs_when_guards_pass_and_records_today_as_last_run_date()
    {
        WritePastWindowFile();
        FillMemoryWindow();
        var config = new AgentConfig { EmbeddingProvider = "fake", IndexRunAtHour = 2, MemoryDays = 3 };
        var host = Build(config, () => ThreeAmCopenhagen);

        await host.CheckAndRunAsync(CancellationToken.None);

        Assert.Equal("2026-04-18", _store.GetLastRunDate());
        // Side effect observable: the past-window file got processed and deleted
        Assert.False(File.Exists(Path.Combine(_memoryDir, "2026-04-10.md")));
    }

    [Fact]
    public async Task Second_call_same_day_does_not_run_pipeline_again()
    {
        WritePastWindowFile();
        FillMemoryWindow();
        var config = new AgentConfig { EmbeddingProvider = "fake", IndexRunAtHour = 2, MemoryDays = 3 };
        var host = Build(config, () => ThreeAmCopenhagen);

        // First call runs: file gets processed and deleted
        await host.CheckAndRunAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_memoryDir, "2026-04-10.md")));

        // Put a fresh past-window file in place. If the second call re-runs, this one would
        // also be processed (deleted); if the guard skips correctly, it stays.
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-09.md"), new string('y', 80));
        await host.CheckAndRunAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-09.md")),
            "second call on the same day must skip (DB-derived guard kicked in)");
    }

    [Fact]
    public async Task New_day_triggers_new_run_after_midnight()
    {
        WritePastWindowFile("2026-04-10");
        FillMemoryWindow();
        var config = new AgentConfig
        {
            EmbeddingProvider = "fake", IndexRunAtHour = 2, MemoryDays = 3,
            CompactionProvider = "fake", CompactionModel = "fake-model",
        };

        var now = ThreeAmCopenhagen;
        var host = Build(config, () => now);

        await host.CheckAndRunAsync(CancellationToken.None);
        Assert.Equal("2026-04-18", _store.GetLastRunDate());

        // Advance the clock one day; drop a fresh past-window file
        now = ThreeAmCopenhagenNextDay;
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-11.md"), new string('z', 80));

        await host.CheckAndRunAsync(CancellationToken.None);

        Assert.Equal("2026-04-19", _store.GetLastRunDate());
        Assert.False(File.Exists(Path.Combine(_memoryDir, "2026-04-11.md")),
            "crossing midnight should re-enable the run and process the new file");
    }

    [Fact]
    public async Task Persisted_last_run_date_survives_host_recreation()
    {
        WritePastWindowFile();
        FillMemoryWindow();
        var config = new AgentConfig { EmbeddingProvider = "fake", IndexRunAtHour = 2, MemoryDays = 3 };

        var first = Build(config, () => ThreeAmCopenhagen);
        await first.CheckAndRunAsync(CancellationToken.None);
        Assert.Equal("2026-04-18", _store.GetLastRunDate());

        // Simulate a process restart: fresh host, same DB, same "today"
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-09.md"), new string('a', 80));
        var second = Build(config, () => ThreeAmCopenhagen);
        await second.CheckAndRunAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-09.md")),
            "persisted last_run_date must suppress the run after restart");
    }
}
