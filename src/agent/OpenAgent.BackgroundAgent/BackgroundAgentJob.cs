using OpenAgent.Contracts;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// Thin <see cref="ISystemJob"/> wrapper that registers the background agent with
/// <c>SystemJobRunner</c>. All the real logic — three-gate check, kickoff turn, sandbox
/// preview — lives in <see cref="BackgroundAgentRunner"/>; this class is just the
/// cron + name + delegation.
/// </summary>
public sealed class BackgroundAgentJob : ISystemJob
{
    private readonly BackgroundAgentRunner _runner;

    public BackgroundAgentJob(BackgroundAgentRunner runner)
    {
        _runner = runner;
    }

    public string Name => BackgroundAgentRunner.JobName;

    /// <summary>Every 15 minutes, between 06:00 and 22:00 inclusive of the start hour.</summary>
    public string Cron => "*/15 6-21 * * *";

    public string Timezone => "Europe/Copenhagen";

    public Task<bool> ShouldRunAsync(CancellationToken ct) =>
        _runner.ShouldRunAsync(DateTimeOffset.UtcNow);

    public Task RunAsync(CancellationToken ct) => _runner.RunAsync(ct);
}
