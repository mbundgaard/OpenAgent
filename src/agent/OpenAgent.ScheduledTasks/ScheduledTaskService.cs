using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.ScheduledTasks.Models;
using OpenAgent.ScheduledTasks.Storage;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// Hosted service that manages the scheduled task lifecycle: loading, scheduling,
/// executing, and persisting tasks. All mutations are serialized through a semaphore.
/// </summary>
public sealed class ScheduledTaskService : IHostedService, IDisposable
{
    private readonly ScheduledTaskStore _store;
    private readonly ScheduledTaskExecutor _executor;
    private readonly DeliveryRouter _deliveryRouter;
    private readonly ILogger<ScheduledTaskService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly int _maxConcurrentRuns;
    private Timer? _timer;

    /// <summary>
    /// Creates a new scheduled task service.
    /// </summary>
    internal ScheduledTaskService(
        ScheduledTaskStore store,
        ScheduledTaskExecutor executor,
        DeliveryRouter deliveryRouter,
        ILogger<ScheduledTaskService> logger,
        int maxConcurrentRuns = 3)
    {
        _store = store;
        _executor = executor;
        _deliveryRouter = deliveryRouter;
        _logger = logger;
        _maxConcurrentRuns = maxConcurrentRuns;
    }

    /// <summary>
    /// Loads tasks from disk, recomputes next runs, executes missed tasks, and arms the timer.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ScheduledTaskService starting");

        // Load persisted tasks
        _store.Load();
        _logger.LogInformation("Loaded {Count} scheduled tasks", _store.Tasks.Count);

        // Recompute all next run times and persist (skip write when empty — nothing changed)
        RecomputeAllNextRuns();
        if (_store.Tasks.Count > 0)
            _store.Save();

        // Run any missed tasks before arming the timer
        await RunMissedTasksAsync(cancellationToken);

        // Arm the timer — tick every 30 seconds
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        _logger.LogInformation("ScheduledTaskService started with 30s tick interval");
    }

    /// <summary>
    /// Stops the timer.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ScheduledTaskService stopping");
        _timer?.Change(Timeout.Infinite, 0);
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes the timer and semaphore.
    /// </summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _lock.Dispose();
    }

    /// <summary>
    /// Adds a new scheduled task. Validates the schedule and computes the first run time.
    /// </summary>
    public async Task AddAsync(ScheduledTask task, CancellationToken ct)
    {
        var error = ScheduleCalculator.Validate(task.Schedule);
        if (error is not null)
            throw new ArgumentException(error);

        await _lock.WaitAsync(ct);
        try
        {
            task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, DateTimeOffset.UtcNow);
            _store.Add(task);
            _store.Save();
            _logger.LogInformation("Added scheduled task '{Name}' ({Id}), next run: {NextRun}",
                task.Name, task.Id, task.State.NextRunAt);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Updates a task by applying a patch action. Re-validates the schedule and recomputes the next run.
    /// </summary>
    public async Task UpdateAsync(string taskId, Action<ScheduledTask> patch, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var task = _store.Get(taskId)
                ?? throw new KeyNotFoundException($"Task '{taskId}' not found.");

            patch(task);

            var error = ScheduleCalculator.Validate(task.Schedule);
            if (error is not null)
                throw new ArgumentException(error);

            task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, DateTimeOffset.UtcNow);
            _store.Save();
            _logger.LogInformation("Updated scheduled task '{Name}' ({Id}), next run: {NextRun}",
                task.Name, task.Id, task.State.NextRunAt);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes a task by ID.
    /// </summary>
    public async Task RemoveAsync(string taskId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _store.Remove(taskId);
            _store.Save();
            _logger.LogInformation("Removed scheduled task '{Id}'", taskId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns a copy of all tasks.
    /// </summary>
    public async Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _store.Tasks.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns a task by ID, or null if not found.
    /// </summary>
    public async Task<ScheduledTask?> GetAsync(string taskId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _store.Get(taskId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Immediately runs a task regardless of schedule. Acquires the lock only for the initial
    /// lookup, then executes outside the lock.
    /// </summary>
    public async Task RunNowAsync(string taskId, string? promptOverride, CancellationToken ct)
    {
        ScheduledTask task;
        await _lock.WaitAsync(ct);
        try
        {
            task = _store.Get(taskId)
                ?? throw new KeyNotFoundException($"Task '{taskId}' not found.");
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Running task '{Name}' ({Id}) on demand", task.Name, task.Id);
        await ExecuteTaskAsync(task, promptOverride, ct);
    }

    /// <summary>
    /// Timer tick — finds due tasks and executes up to maxConcurrentRuns concurrently.
    /// </summary>
    private async Task TickAsync()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            List<ScheduledTask> dueTasks;

            await _lock.WaitAsync();
            try
            {
                dueTasks = _store.Tasks
                    .Where(t => t.Enabled && t.State.NextRunAt.HasValue && t.State.NextRunAt.Value <= now)
                    .Take(_maxConcurrentRuns)
                    .ToList();
            }
            finally
            {
                _lock.Release();
            }

            if (dueTasks.Count == 0)
                return;

            _logger.LogInformation("Tick: {Count} task(s) due for execution", dueTasks.Count);

            // Execute concurrently
            var executions = dueTasks.Select(t => ExecuteTaskAsync(t, promptOverride: null, CancellationToken.None));
            await Task.WhenAll(executions);
        }
        catch (Exception ex)
        {
            // Catch all to prevent the timer from dying
            _logger.LogError(ex, "Error in scheduled task tick");
        }
    }

    /// <summary>
    /// Executes a single task: runs the LLM completion, delivers the result, then updates state under the lock.
    /// </summary>
    private async Task ExecuteTaskAsync(ScheduledTask task, string? promptOverride, CancellationToken ct)
    {
        try
        {
            // Execute and deliver OUTSIDE the lock
            var response = await _executor.ExecuteAsync(task, promptOverride, ct);
            await _deliveryRouter.DeliverAsync(task, response, ct);

            // Update state UNDER the lock
            await _lock.WaitAsync(ct);
            try
            {
                task.State.LastRunAt = DateTimeOffset.UtcNow;
                task.State.LastStatus = TaskRunStatus.Success;
                task.State.LastError = null;
                task.State.ConsecutiveErrors = 0;
                task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, DateTimeOffset.UtcNow);

                // Remove one-shot tasks after successful execution
                if (task.DeleteAfterRun)
                {
                    _store.Remove(task.Id);
                    _logger.LogInformation("Task '{Name}' ({Id}) removed after successful run (deleteAfterRun)", task.Name, task.Id);
                }

                _store.Save();
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task '{Name}' ({Id}) failed", task.Name, task.Id);

            await _lock.WaitAsync(ct);
            try
            {
                task.State.LastRunAt = DateTimeOffset.UtcNow;
                task.State.LastStatus = TaskRunStatus.Error;
                task.State.LastError = ex.Message;
                task.State.ConsecutiveErrors++;
                task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, DateTimeOffset.UtcNow);
                _store.Save();
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>
    /// Runs tasks that were missed while the service was offline. Executes sequentially with 2-second stagger.
    /// </summary>
    private async Task RunMissedTasksAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var missedTasks = _store.Tasks
            .Where(t => t.Enabled && t.State.NextRunAt.HasValue && t.State.NextRunAt.Value < now)
            .ToList();

        if (missedTasks.Count == 0)
            return;

        _logger.LogInformation("Found {Count} missed task(s), running sequentially", missedTasks.Count);

        foreach (var task in missedTasks)
        {
            _logger.LogInformation("Running missed task '{Name}' ({Id})", task.Name, task.Id);
            await ExecuteTaskAsync(task, promptOverride: null, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>
    /// Recomputes NextRunAt for all enabled tasks.
    /// </summary>
    private void RecomputeAllNextRuns()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var task in _store.Tasks)
        {
            if (!task.Enabled) continue;
            task.State.NextRunAt = ScheduleCalculator.ComputeNextRun(task.Schedule, now);
        }
    }
}
