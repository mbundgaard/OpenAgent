using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Models.Configs;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// Hourly hosted service that drives the memory indexing job. Calls <see cref="MemoryIndexService.RunAsync"/>
/// on every tick — RunAsync is idempotent (skips dates already in the index), so letting it run freely
/// has no cost when nothing new is past the memory window.
/// </summary>
public sealed class MemoryIndexHostedService : IHostedService, IDisposable
{
    private readonly MemoryIndexService _service;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<MemoryIndexHostedService> _logger;

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private bool _warnedMissingProvider;

    public MemoryIndexHostedService(
        MemoryIndexService service,
        AgentConfig agentConfig,
        ILogger<MemoryIndexHostedService> logger)
    {
        _service = service;
        _agentConfig = agentConfig;
        _logger = logger;
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
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _timer?.Dispose(); } catch (ObjectDisposedException) { }
        _timer = null;
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { /* loop already torn itself down */ }
        }
        _logger.LogInformation("MemoryIndexHostedService stopped");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // The whole loop must catch its own exceptions: this Task is observed by StopAsync
        // via WaitAsync, and any unhandled exception there would surface as an
        // ObjectDisposedException / generic fault during xUnit's IClassFixture teardown.
        try
        {
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
                catch (ObjectDisposedException) { return; } // host is tearing down — exit cleanly
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MemoryIndexHostedService tick threw; continuing");
                }
            }
        }
        catch (OperationCanceledException) { /* host stopping — normal */ }
        catch (ObjectDisposedException) { /* host stopping — normal */ }
    }

    /// <summary>
    /// Single guard: the embedding provider must be configured. Otherwise RunAsync would throw
    /// trying to resolve an empty-keyed singleton. Exposed as internal for test access.
    /// </summary>
    internal async Task CheckAndRunAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_agentConfig.EmbeddingProvider))
        {
            if (!_warnedMissingProvider)
            {
                _logger.LogWarning("Memory index: embeddingProvider is empty; indexing disabled");
                _warnedMissingProvider = true;
            }
            return;
        }

        try
        {
            var result = await _service.RunAsync(ct);
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
}
