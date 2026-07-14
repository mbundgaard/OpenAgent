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

    // A gated-out tick must never fabricate run history for a job that has never actually run -
    // RunCount/LastRunAt/LastStatus stay at their "never ran" defaults. NextRunAt is a different
    // story: ExecuteAsync recomputes it on every tick (gated or not) so it always points at a
    // real future cron slot instead of going stale, so we only assert it is still a valid future
    // value rather than pinning it to the exact instant StartAsync happened to seed - on an
    // hourly cron the two calls can legitimately land on the same slot, and asserting equality
    // would make the test pass for the wrong reason near an hour boundary.
    //
    // This is distinct from Gated_out_tick_advances_next_run_to_the_next_cron_slot and
    // Gated_out_tick_does_not_touch_last_run_at below: those drive ExecuteAsync directly against
    // a job that already has run history (a pre-set LastRunAt and a stale NextRunAt), to prove
    // existing data survives a gated tick. This test drives the full StartAsync/ExecuteAsync
    // lifecycle for a job with no history yet, to prove a gated tick does not create any.
    [Fact]
    public async Task ShouldRunAsync_false_skips_execution_and_advances_next_run_without_creating_run_history()
    {
        var job = new GatedJob("gated", "0 * * * *", "UTC") { Allow = false };
        var runner = BuildRunner(job);
        await runner.StartAsync(CancellationToken.None);

        await runner.ExecuteAsync(job, CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        var state = runner.GetState("gated")!;
        Assert.Equal(0, job.RunCount);
        Assert.Null(state.LastRunAt);
        Assert.Null(state.LastStatus);
        Assert.NotNull(state.NextRunAt);
        Assert.True(state.NextRunAt > DateTimeOffset.UtcNow, $"gated tick must recompute a future cron slot, was {state.NextRunAt}");
    }

    [Fact]
    public async Task ShouldRunAsync_true_runs_normally()
    {
        var job = new GatedJob("opened", "0 * * * *", "UTC") { Allow = true };
        var runner = BuildRunner(job);
        await runner.StartAsync(CancellationToken.None);

        await runner.ExecuteAsync(job, CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        Assert.Equal(1, job.RunCount);
        var state = runner.GetState("opened")!;
        Assert.Equal("success", state.LastStatus);
    }

    [Fact]
    public async Task ShouldRunAsync_throwing_is_treated_as_skip()
    {
        var job = new GatedJob("throwing", "0 * * * *", "UTC") { ThrowOnGate = true };
        var runner = BuildRunner(job);
        await runner.StartAsync(CancellationToken.None);

        await runner.ExecuteAsync(job, CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        Assert.Equal(0, job.RunCount);
        var state = runner.GetState("throwing")!;
        Assert.Null(state.LastRunAt);
    }

    // Regression: a gated-out tick used to leave NextRunAt in the past, so the job stayed
    // permanently "due" and fired as soon as its interval gate opened - outside the cron
    // window. Observed in production at 22:54 CPH against a "6-21" cron.
    [Fact]
    public async Task Gated_out_tick_advances_next_run_to_the_next_cron_slot()
    {
        var store = new SystemJobStateStore(_statePath);
        store.Load();
        var job = new CronWindowGatedJob();
        var runner = new SystemJobRunner([job], store, NullLogger<SystemJobRunner>.Instance);

        var state = store.GetOrCreate(job.Name);
        var stale = DateTimeOffset.UtcNow.AddHours(-3);
        state.NextRunAt = stale;

        await runner.ExecuteAsync(job, CancellationToken.None);

        Assert.False(job.Ran);
        Assert.NotNull(state.NextRunAt);
        Assert.True(state.NextRunAt > DateTimeOffset.UtcNow,
            $"gated-out tick must push NextRunAt into the future, was {state.NextRunAt}");
    }

    // A gated-out tick must NOT touch LastRunAt - the interval gates in BackgroundAgentRunner
    // are computed from it, and resetting it would starve them forever.
    [Fact]
    public async Task Gated_out_tick_does_not_touch_last_run_at()
    {
        var store = new SystemJobStateStore(_statePath);
        store.Load();
        var job = new CronWindowGatedJob();
        var runner = new SystemJobRunner([job], store, NullLogger<SystemJobRunner>.Instance);

        var state = store.GetOrCreate(job.Name);
        var lastRun = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.LastRunAt = lastRun;
        state.NextRunAt = DateTimeOffset.UtcNow.AddHours(-1);

        await runner.ExecuteAsync(job, CancellationToken.None);

        Assert.Equal(lastRun, state.LastRunAt);
    }

    // Regression for the fix landed alongside the NextRunAt-advance fix above: the runner ticks
    // every 60 seconds and is gated out most ticks, so recomputing NextRunAt on every gated tick
    // must NOT translate into a disk write on every gated tick when the recomputed value is the
    // same cron slot as what is already stored. Proven via the state file's last-write timestamp:
    // a real write updates it, a skipped write leaves it exactly where it was.
    [Fact]
    public async Task Gated_out_tick_with_unchanged_NextRunAt_does_not_rewrite_the_file()
    {
        var store = new SystemJobStateStore(_statePath);
        store.Load();
        var job = new CronWindowGatedJob();
        var runner = new SystemJobRunner([job], store, NullLogger<SystemJobRunner>.Instance);

        // First tick: stored NextRunAt is stale, so this recomputes and writes - establishes a
        // real, current cron-slot value on disk to compare against.
        var state = store.GetOrCreate(job.Name);
        state.NextRunAt = DateTimeOffset.UtcNow.AddHours(-3);
        await runner.ExecuteAsync(job, CancellationToken.None);
        Assert.True(File.Exists(_statePath));
        var nextRunAtAfterFirstTick = state.NextRunAt;
        var lastWriteTimeAfterFirstTick = File.GetLastWriteTimeUtc(_statePath);

        // Small delay so that, if a second write DID happen, the filesystem timestamp would
        // almost certainly differ from the first - this makes an equal timestamp meaningful.
        await Task.Delay(50);

        // Second tick lands well within the same 15-minute cron slot, so the recomputed
        // NextRunAt equals what is already stored - this tick must not touch the file at all.
        await runner.ExecuteAsync(job, CancellationToken.None);

        Assert.Equal(nextRunAtAfterFirstTick, state.NextRunAt);
        Assert.Equal(lastWriteTimeAfterFirstTick, File.GetLastWriteTimeUtc(_statePath));
    }

    // Fixed-shape job used by the NextRunAt-advance regression tests: always gates out via
    // ShouldRunAsync, on a cron with a restricted daily window ("6-21") so a leaked stale
    // NextRunAt would visibly fire outside that window.
    private sealed class CronWindowGatedJob : ISystemJob
    {
        public bool Ran { get; private set; }
        public string Name => "gated-job";
        public string Cron => "*/15 6-21 * * *";
        public string Timezone => "Europe/Copenhagen";
        public Task<bool> ShouldRunAsync(CancellationToken ct) => Task.FromResult(false);
        public Task RunAsync(CancellationToken ct) { Ran = true; return Task.CompletedTask; }
    }

    private sealed class GatedJob : ISystemJob
    {
        public GatedJob(string name, string cron, string tz)
        {
            Name = name; Cron = cron; Timezone = tz;
        }
        public string Name { get; }
        public string Cron { get; }
        public string Timezone { get; }
        public bool Allow { get; set; }
        public bool ThrowOnGate { get; set; }
        public int RunCount { get; private set; }
        public Task<bool> ShouldRunAsync(CancellationToken ct)
        {
            if (ThrowOnGate) throw new InvalidOperationException("gate failed");
            return Task.FromResult(Allow);
        }
        public Task RunAsync(CancellationToken ct)
        {
            RunCount++;
            return Task.CompletedTask;
        }
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
