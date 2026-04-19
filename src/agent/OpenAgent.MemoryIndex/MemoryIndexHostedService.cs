using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Hourly hosted service that drives the nightly memory indexing job. Runs once per local
/// day, at or after the configured hour. "Did it already run today?" is persisted in the
/// job_state table so a container restart between 02:00 and 03:00 doesn't cause a second
/// run the same day.
/// </summary>
public sealed class MemoryIndexHostedService : IHostedService, IDisposable
{
    private static readonly TimeZoneInfo CopenhagenTz = ResolveCopenhagen();

    private readonly MemoryIndexService _service;
    private readonly MemoryChunkStore _store;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<MemoryIndexHostedService> _logger;
    private readonly Func<DateTimeOffset> _clock;

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private bool _warnedMissingProvider;

    public MemoryIndexHostedService(
        MemoryIndexService service,
        MemoryChunkStore store,
        AgentConfig agentConfig,
        ILogger<MemoryIndexHostedService> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _service = service;
        _store = store;
        _agentConfig = agentConfig;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(TimeSpan.FromHours(1));
        _loopTask = LoopAsync(_cts.Token);
        _logger.LogInformation("MemoryIndexHostedService started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
        _logger.LogInformation("MemoryIndexHostedService stopped");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Fire an immediate check on startup; subsequent checks run hourly
        await CheckAndRunAsync(ct);

        var timer = _timer;
        if (timer is null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) return;
                await CheckAndRunAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MemoryIndexHostedService tick threw; continuing");
            }
        }
    }

    /// <summary>
    /// Evaluates all guards and, if all pass, triggers an indexing run. Exposed as internal
    /// so tests can exercise the decision logic without running the periodic loop.
    /// </summary>
    internal async Task CheckAndRunAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_agentConfig.EmbeddingProvider))
        {
            if (!_warnedMissingProvider)
            {
                _logger.LogWarning("Memory index: embeddingProvider is empty; daily job is disabled");
                _warnedMissingProvider = true;
            }
            return;
        }

        var localNow = TimeZoneInfo.ConvertTime(_clock(), CopenhagenTz);
        if (localNow.Hour < _agentConfig.IndexRunAtHour)
        {
            _logger.LogDebug("Memory index: skip — local hour {Hour} < {Threshold}", localNow.Hour, _agentConfig.IndexRunAtHour);
            return;
        }

        var today = localNow.ToString("yyyy-MM-dd");
        if (_store.GetLastRunDate() == today)
        {
            _logger.LogDebug("Memory index: skip — already ran today ({Date})", today);
            return;
        }

        try
        {
            var result = await _service.RunAsync(ct);
            _store.SetLastRunDate(today);
            _logger.LogInformation(
                "Memory index: scanned={Scanned} processed={Processed} discarded={Discarded} chunks={Chunks} errors={Errors}",
                result.FilesScanned, result.FilesProcessed, result.FilesDiscarded, result.ChunksCreated, result.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory index run failed; will retry on next tick");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Dispose();
    }

    // Linux containers ship the IANA name, Windows ships the Windows name. Prefer IANA,
    // fall back to the Windows zone so development on Windows works.
    private static TimeZoneInfo ResolveCopenhagen()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"); }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        }
    }
}
