using OpenAgent.Contracts;

namespace OpenAgent.MemoryDigest;

/// <summary>
/// Wraps <see cref="MemoryDigestService"/> as an <see cref="ISystemJob"/> so the
/// <c>SystemJobRunner</c> can drive it on a nightly cron. Hardcoded to 03:00 Europe/Copenhagen —
/// not user-editable, by design.
/// </summary>
public sealed class MemoryDigestSystemJob : ISystemJob
{
    private readonly MemoryDigestService _service;

    public MemoryDigestSystemJob(MemoryDigestService service)
    {
        _service = service;
    }

    public string Name => "memory-digest";

    /// <summary>Nightly at 03:00 — quiet hours, after the index job's typical activity.</summary>
    public string Cron => "0 3 * * *";

    public string Timezone => "Europe/Copenhagen";

    public Task RunAsync(CancellationToken ct) => _service.RunAsync(ct);
}
