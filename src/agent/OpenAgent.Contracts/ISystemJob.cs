namespace OpenAgent.Contracts;

/// <summary>
/// A background job that fires on a cron schedule and is not editable by the user. Implemented
/// by maintenance work like the memory digest. Distinct from user-defined ScheduledTask: a system
/// job has no conversation, no delivery, no LLM-prompt contract — it just runs.
/// </summary>
public interface ISystemJob
{
    /// <summary>
    /// Unique identifier for this job — used as the key in the persisted state file. Must be
    /// stable across restarts (e.g. <c>"memory-digest"</c>) so missed-run recovery works.
    /// </summary>
    string Name { get; }

    /// <summary>Cron expression in the standard 5-field format (minute hour dom month dow).</summary>
    string Cron { get; }

    /// <summary>IANA timezone for the cron expression (e.g. <c>"Europe/Copenhagen"</c>).</summary>
    string Timezone { get; }

    /// <summary>
    /// Execute one run of the job. Implementations should be idempotent — the runner guarantees
    /// at-least-once delivery but a clean restart can re-fire a run that was already in flight.
    /// </summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>
    /// Optional pre-flight gate. Called by <c>SystemJobRunner</c> when the cron says it's time —
    /// returning false skips execution and leaves <c>LastRunAt</c> untouched (interval gates that
    /// read it stay accurate), but <c>NextRunAt</c> IS still advanced to the next cron slot so a
    /// gated-out tick does not leave the job permanently "due". Useful for jobs that need to
    /// evaluate runtime conditions beyond the cron expression (e.g. "skip unless the user has been
    /// idle for 45min"). Default returns true — the job runs whenever the cron fires.
    /// </summary>
    Task<bool> ShouldRunAsync(CancellationToken ct) => Task.FromResult(true);
}
