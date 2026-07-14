using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// DI registration for the background agent. Adds the runner, the ISystemJob wrapper, and the
/// startup sweep that cleans up orphaned heartbeat nudges left behind by a hard kill mid-turn.
/// Requires <c>AddSystemJobs</c> to have been registered separately - the system-job runner picks
/// the wrapper up automatically via <c>IEnumerable&lt;ISystemJob&gt;</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundAgent(this IServiceCollection services)
    {
        services.AddSingleton<BackgroundAgentRunner>();
        services.AddSingleton<ISystemJob, BackgroundAgentJob>();
        services.AddSingleton<BackgroundAgentNudgeSweep>();
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundAgentNudgeSweep>());
        return services;
    }
}
