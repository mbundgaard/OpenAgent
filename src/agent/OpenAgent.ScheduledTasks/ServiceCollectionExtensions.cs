using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.ScheduledTasks.Storage;
using OpenAgent.ScheduledTasks.SystemJobs;

namespace OpenAgent.ScheduledTasks;

/// <summary>
/// DI registration for the scheduled tasks subsystem.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all scheduled task services: store, executor, delivery router,
    /// hosted service, tool handler, and HTTP client.
    /// </summary>
    public static IServiceCollection AddScheduledTasks(this IServiceCollection services, string dataPath)
    {
        var storePath = Path.Combine(dataPath, "config", "scheduled-tasks.json");

        // Internal classes require factory lambdas for DI registration
        services.AddSingleton(new ScheduledTaskStore(storePath));
        services.AddSingleton(sp => new ScheduledTaskExecutor(
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<Func<string, ILlmTextProvider>>(),
            sp.GetRequiredService<AgentConfig>(),
            sp.GetRequiredService<ILogger<ScheduledTaskExecutor>>()));
        services.AddSingleton(sp => new DeliveryRouter(
            sp.GetRequiredService<IConnectionManager>(),
            sp.GetRequiredService<IWebSocketRegistry>(),
            sp.GetRequiredService<ILogger<DeliveryRouter>>()));
        services.AddSingleton(sp => new ScheduledTaskService(
            sp.GetRequiredService<ScheduledTaskStore>(),
            sp.GetRequiredService<ScheduledTaskExecutor>(),
            sp.GetRequiredService<DeliveryRouter>(),
            sp.GetRequiredService<ILogger<ScheduledTaskService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<ScheduledTaskService>());
        services.AddSingleton<IToolHandler, ScheduledTaskToolHandler>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="SystemJobRunner"/> hosted service. <see cref="ISystemJob"/>
    /// implementations registered separately on the container are picked up automatically via
    /// <c>IEnumerable&lt;ISystemJob&gt;</c>.
    /// </summary>
    public static IServiceCollection AddSystemJobs(this IServiceCollection services, string dataPath)
    {
        var statePath = Path.Combine(dataPath, "config", "system-jobs.json");
        services.AddSingleton(new SystemJobStateStore(statePath));
        services.AddSingleton<SystemJobRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<SystemJobRunner>());
        return services;
    }
}
