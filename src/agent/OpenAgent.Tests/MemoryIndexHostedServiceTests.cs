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

    private MemoryIndexService BuildService(AgentConfig config)
    {
        var provider = new StreamingTextProvider("""{"chunks":[{"content":"topic","summary":"topic summary"}]}""");
        var chunker = new MemoryChunker(_ => provider, config);
        return new MemoryIndexService(
            _store,
            chunker,
            _ => new FakeEmbeddingProvider(),
            config,
            _env,
            NullLogger<MemoryIndexService>.Instance);
    }

    private MemoryIndexHostedService Build(AgentConfig config) =>
        new(BuildService(config), config, NullLogger<MemoryIndexHostedService>.Instance);

    private void WritePastWindowFile(string date = "2026-04-10") =>
        File.WriteAllText(Path.Combine(_memoryDir, $"{date}.md"), new string('x', 80));

    private void FillMemoryWindow()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-18.md"), new string('a', 80));
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-17.md"), new string('b', 80));
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-16.md"), new string('c', 80));
    }

    [Fact]
    public async Task Skips_when_embedding_provider_is_empty()
    {
        WritePastWindowFile();
        FillMemoryWindow();

        var config = new AgentConfig
        {
            EmbeddingProvider = "",
            CompactionProvider = "fake", CompactionModel = "fake-model", MemoryDays = 3,
        };
        var host = Build(config);

        await host.CheckAndRunAsync(CancellationToken.None);

        // Provider empty → no run, so candidates stay on disk
        Assert.True(File.Exists(Path.Combine(_memoryDir, "2026-04-10.md")));
        Assert.Empty(_store.GetProcessedDates());
    }

    [Fact]
    public async Task Runs_pipeline_when_embedding_provider_is_set()
    {
        WritePastWindowFile();
        FillMemoryWindow();

        var config = new AgentConfig
        {
            EmbeddingProvider = "fake",
            CompactionProvider = "fake", CompactionModel = "fake-model", MemoryDays = 3,
        };
        var host = Build(config);

        await host.CheckAndRunAsync(CancellationToken.None);

        // Past-window file got processed and deleted; index has the date
        Assert.False(File.Exists(Path.Combine(_memoryDir, "2026-04-10.md")));
        Assert.Contains("2026-04-10", _store.GetProcessedDates());
    }

    [Fact]
    public async Task Repeated_calls_are_idempotent()
    {
        WritePastWindowFile();
        FillMemoryWindow();

        var config = new AgentConfig
        {
            EmbeddingProvider = "fake",
            CompactionProvider = "fake", CompactionModel = "fake-model", MemoryDays = 3,
        };
        var host = Build(config);

        await host.CheckAndRunAsync(CancellationToken.None);
        var firstStats = _store.GetStats();

        // Drop a fresh past-window file and call again — should process only the new file,
        // never re-process the already-indexed one (inner alreadyProcessed check).
        File.WriteAllText(Path.Combine(_memoryDir, "2026-04-09.md"), new string('y', 80));
        await host.CheckAndRunAsync(CancellationToken.None);

        var secondStats = _store.GetStats();
        Assert.True(secondStats.TotalChunks > firstStats.TotalChunks,
            "second call should index the new file but not re-process the old one");
        Assert.False(File.Exists(Path.Combine(_memoryDir, "2026-04-09.md")));
    }
}
