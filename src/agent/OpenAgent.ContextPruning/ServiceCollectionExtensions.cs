using Microsoft.Extensions.DependencyInjection;

namespace OpenAgent.ContextPruning;

/// <summary>
/// DI registration for the context pruning subsystem. Assumes the host has already registered
/// <see cref="Models.Configs.AgentConfig"/> and <see cref="Contracts.IConversationStore"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddContextPruning(this IServiceCollection services)
    {
        // Lazy resolution breaks the circular dep: store -> IContextPruneTrigger -> service -> store.
        services.AddSingleton(sp => new Lazy<Contracts.IConversationStore>(
            () => sp.GetRequiredService<Contracts.IConversationStore>()));
        services.AddSingleton<ContextPruneService>();
        services.AddSingleton<Contracts.IContextPruneTrigger>(sp => sp.GetRequiredService<ContextPruneService>());
        services.AddHostedService<ContextPruneHostedService>();
        return services;
    }
}
