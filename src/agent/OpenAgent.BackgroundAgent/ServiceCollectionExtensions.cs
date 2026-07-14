using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;

namespace OpenAgent.BackgroundAgent;

/// <summary>
/// DI registration for the background agent. Adds the runner and the ISystemJob wrapper.
/// Requires <c>AddSystemJobs</c> to have been registered separately - the system-job runner picks
/// the wrapper up automatically via <c>IEnumerable&lt;ISystemJob&gt;</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundAgent(this IServiceCollection services)
    {
        services.AddSingleton<BackgroundAgentRunner>();
        services.AddSingleton<ISystemJob, BackgroundAgentJob>();
        return services;
    }
}
