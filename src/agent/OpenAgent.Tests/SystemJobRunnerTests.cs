using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.ScheduledTasks.SystemJobs;

namespace OpenAgent.Tests;

public class SystemJobRunnerTests : IDisposable
{
    private readonly string _statePath;

    public SystemJobRunnerTests()
    {
        _statePath = Path.Combine(Path.GetTempPath(), "openagent-systemjobs-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_statePath); } catch { /* best-effort */ }
    }

    private SystemJobRunner BuildRunner(params ISystemJob[] jobs) =>
        new(jobs, new SystemJobStateStore(_statePath), NullLogger<SystemJobRunner>.Instance);

    [Fact]
    public async Task ExecuteAsync_records_success_and_schedules_next_run()
    {
        var job = new FakeJob("test", "*/5 * * * *", "UTC");
        var runner = BuildRunner(job);
        await runner.StartAsync(CancellationToken.None);

        await runner.ExecuteAsync(job, CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        var state = runner.GetState("test");
        Assert.NotNull(state);
        Assert.Equal(1, job.RunCount);
        Assert.Equal("success", state!.LastStatus);
        Assert.Null(state.LastError);
        Assert.NotNull(state.LastRunAt);
        Assert.NotNull(state.NextRunAt);
        Assert.True(state.NextRunAt > state.LastRunAt);
    }

    [Fact]
    public async Task ExecuteAsync_records_error_and_increments_consecutive_count()
    {
        var job = new ThrowingJob("flaky", "0 * * * *", "UTC", new InvalidOperationException("boom"));
        var runner = BuildRunner(job);
        await runner.StartAsync(CancellationToken.None);

        await runner.ExecuteAsync(job, CancellationToken.None);
        await runner.ExecuteAsync(job, CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        var state = runner.GetState("flaky")!;
        Assert.Equal("error", state.LastStatus);
        Assert.Equal("boom", state.LastError);
        Assert.Equal(2, state.ConsecutiveErrors);
    }

    [Fact]
    public async Task Success_after_failure_resets_consecutive_errors()
    {
        var job = new ToggleJob("toggle", "0 * * * *", "UTC");
        var runner = BuildRunner(job);
        await runner.StartAsync(CancellationToken.None);

        job.ShouldThrow = true;
        await runner.ExecuteAsync(job, CancellationToken.None);
        job.ShouldThrow = false;
        await runner.ExecuteAsync(job, CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        var state = runner.GetState("toggle")!;
        Assert.Equal("success", state.LastStatus);
        Assert.Equal(0, state.ConsecutiveErrors);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task StartAsync_seeds_NextRunAt_for_jobs_without_existing_state()
    {
        var job = new FakeJob("fresh", "0 3 * * *", "Europe/Copenhagen");
        var runner = BuildRunner(job);

        await runner.StartAsync(CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        var state = runner.GetState("fresh")!;
        Assert.NotNull(state.NextRunAt);
        Assert.True(state.NextRunAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task State_is_persisted_across_runner_instances()
    {
        var job = new FakeJob("persist", "0 * * * *", "UTC");

        var first = BuildRunner(job);
        await first.StartAsync(CancellationToken.None);
        await first.ExecuteAsync(job, CancellationToken.None);
        await first.StopAsync(CancellationToken.None);

        // Second runner reading the same state file should see the first run's outcome.
        var second = BuildRunner(new FakeJob("persist", "0 * * * *", "UTC"));
        await second.StartAsync(CancellationToken.None);
        await second.StopAsync(CancellationToken.None);

        var state = second.GetState("persist")!;
        Assert.Equal("success", state.LastStatus);
        Assert.NotNull(state.LastRunAt);
    }

    private sealed class FakeJob : ISystemJob
    {
        public FakeJob(string name, string cron, string tz)
        {
            Name = name; Cron = cron; Timezone = tz;
        }
        public string Name { get; }
        public string Cron { get; }
        public string Timezone { get; }
        public int RunCount { get; private set; }
        public Task RunAsync(CancellationToken ct)
        {
            RunCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingJob : ISystemJob
    {
        private readonly Exception _ex;
        public ThrowingJob(string name, string cron, string tz, Exception ex)
        {
            Name = name; Cron = cron; Timezone = tz; _ex = ex;
        }
        public string Name { get; }
        public string Cron { get; }
        public string Timezone { get; }
        public Task RunAsync(CancellationToken ct) => throw _ex;
    }

    private sealed class ToggleJob : ISystemJob
    {
        public ToggleJob(string name, string cron, string tz)
        {
            Name = name; Cron = cron; Timezone = tz;
        }
        public string Name { get; }
        public string Cron { get; }
        public string Timezone { get; }
        public bool ShouldThrow { get; set; }
        public Task RunAsync(CancellationToken ct)
        {
            if (ShouldThrow) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }
    }
}
