using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Models.Configs;

namespace OpenAgent.ContextPruning;

/// <summary>
/// Scheduled catch-up sweep for the context purge. Cadence from AgentConfig.PurgeScheduledIntervalHours
/// (default 1h). Per conversation, the service runs the same transactional purge used by the reactive
/// path; errors on one conversation don't block the others.
/// </summary>
public sealed class ContextPruneHostedService : IHostedService, IDisposable
{
    private readonly ContextPruneService _service;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<ContextPruneHostedService> _logger;

    private PeriodicTimer? _timer;
    private Task? _loopTask;
    private CancellationTokenSource? _cts;

    public ContextPruneHostedService(
        ContextPruneService service,
        AgentConfig agentConfig,
        ILogger<ContextPruneHostedService> logger)
    {
        _service = service;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hours = Math.Max(1, _agentConfig.PurgeScheduledIntervalHours);
        _timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        _loopTask = LoopAsync(_cts.Token);
        _logger.LogInformation("ContextPruneHostedService started (interval={Hours}h)", hours);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _timer?.Dispose();
        _timer = null;
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            _loopTask = null;
        }
        var cts = _cts;
        _cts = null;
        try { cts?.Dispose(); } catch (ObjectDisposedException) { }
        _logger.LogInformation("ContextPruneHostedService stopped");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        RunOnce();

        var timer = _timer;
        if (timer is null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) return;
                RunOnce();
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; } // timer disposed during StopAsync
            catch (Exception ex)
            {
                _logger.LogError(ex, "ContextPruneHostedService tick threw; continuing");
            }
        }
    }

    private void RunOnce()
    {
        try
        {
            _service.PurgeAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Context purge sweep failed; will retry on next tick");
        }
    }

    public void Dispose()
    {
        // StopAsync already disposes _timer; guard against double-dispose races.
        try { _timer?.Dispose(); } catch (ObjectDisposedException) { }
        try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
    }
}
