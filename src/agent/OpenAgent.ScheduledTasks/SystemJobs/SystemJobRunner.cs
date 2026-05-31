using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.ScheduledTasks.Models;

namespace OpenAgent.ScheduledTasks.SystemJobs;

/// <summary>
/// Hosted service that drives all registered <see cref="ISystemJob"/> implementations from a
/// single 60-second tick loop. Reuses <see cref="ScheduleCalculator"/> for cron evaluation,
/// persists per-job state to <see cref="SystemJobStateStore"/>, and recovers missed runs after
/// a restart by treating any job with <c>NextRunAt &lt;= now</c> as due. Distinct from
/// <see cref="ScheduledTaskService"/>: system jobs are code-owned, not user-editable, and have
/// no conversation or delivery semantics.
/// </summary>
public sealed class SystemJobRunner : IHostedService, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<ISystemJob> _jobs;
    private readonly SystemJobStateStore _store;
    private readonly ILogger<SystemJobRunner> _logger;
    private readonly object _lock = new();
    private Timer? _timer;
    private CancellationTokenSource? _cts;

    public SystemJobRunner(
        IEnumerable<ISystemJob> jobs,
        SystemJobStateStore store,
        ILogger<SystemJobRunner> logger)
    {
        _jobs = jobs.ToList();
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.Load();
        _logger.LogInformation("SystemJobRunner starting with {Count} job(s)", _jobs.Count);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in _jobs)
        {
            var state = _store.GetOrCreate(job.Name);
            // Seed NextRunAt on first start so missed-run detection has something to compare.
            if (state.NextRunAt is null)
                state.NextRunAt = ComputeNext(job, state.LastRunAt ?? now);
        }
        _store.Save();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(_ => _ = TickAsync(_cts.Token), null, TimeSpan.Zero, TickInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _timer?.Change(Timeout.Infinite, 0); } catch (ObjectDisposedException) { }
        try { _timer?.Dispose(); } catch (ObjectDisposedException) { }
        _timer = null;
        _logger.LogInformation("SystemJobRunner stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch (ObjectDisposedException) { }
        try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;

            // Pick a snapshot of due jobs under the lock so we don't race with Save.
            List<ISystemJob> dueJobs;
            lock (_lock)
            {
                dueJobs = _jobs
                    .Where(j =>
                    {
                        var state = _store.GetOrCreate(j.Name);
                        return state.NextRunAt is { } next && next <= now;
                    })
                    .ToList();
            }

            // Run sequentially. System jobs are maintenance work; we don't expect overlap and
            // a single bounded thread of work keeps reasoning about state simple.
            foreach (var job in dueJobs)
            {
                if (ct.IsCancellationRequested) return;
                await ExecuteAsync(job, ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ObjectDisposedException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SystemJobRunner tick threw; will retry on next interval");
        }
    }

    /// <summary>
    /// Run a single job, update its state, and persist. Exceptions are caught and recorded —
    /// the runner survives a failing job and retries on its next scheduled occurrence.
    /// </summary>
    public async Task ExecuteAsync(ISystemJob job, CancellationToken ct)
    {
        _logger.LogInformation("System job '{Name}' starting", job.Name);
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await job.RunAsync(ct);

            lock (_lock)
            {
                var state = _store.GetOrCreate(job.Name);
                state.LastRunAt = DateTimeOffset.UtcNow;
                state.LastStatus = "success";
                state.LastError = null;
                state.ConsecutiveErrors = 0;
                state.NextRunAt = ComputeNext(job, state.LastRunAt.Value);
                _store.Save();
            }

            _logger.LogInformation("System job '{Name}' completed in {Ms}ms",
                job.Name, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System job '{Name}' failed", job.Name);

            lock (_lock)
            {
                var state = _store.GetOrCreate(job.Name);
                state.LastRunAt = DateTimeOffset.UtcNow;
                state.LastStatus = "error";
                state.LastError = ex.Message;
                state.ConsecutiveErrors++;
                // Reschedule normally — don't backoff. System jobs are nightly; the next natural
                // tick is the right retry cadence.
                state.NextRunAt = ComputeNext(job, state.LastRunAt.Value);
                _store.Save();
            }
        }
    }

    private static DateTimeOffset? ComputeNext(ISystemJob job, DateTimeOffset after)
    {
        var schedule = new ScheduleConfig { Cron = job.Cron, Timezone = job.Timezone };
        return ScheduleCalculator.ComputeNextRun(schedule, after);
    }

    /// <summary>Read-only snapshot of state for the given job, or null if no state recorded yet.</summary>
    public SystemJobState? GetState(string name) =>
        _store.All.TryGetValue(name, out var state) ? state : null;

    /// <summary>All registered system jobs (by reference — do not mutate).</summary>
    public IReadOnlyList<ISystemJob> Jobs => _jobs;
}
