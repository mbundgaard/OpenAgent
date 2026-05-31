using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryDigest;

/// <summary>
/// DI registration for the memory digest subsystem. The digest itself is a single service +
/// a system job wrapper; the runner that ticks it lives in <c>OpenAgent.ScheduledTasks</c>
/// (registered separately via <c>AddSystemJobs</c>).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryDigest(this IServiceCollection services)
    {
        services.AddSingleton<MemoryDigestService>();
        services.AddSingleton<ISystemJob, MemoryDigestSystemJob>();
        return services;
    }
}
