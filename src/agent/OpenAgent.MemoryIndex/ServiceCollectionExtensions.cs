using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;

namespace OpenAgent.MemoryIndex;

/// <summary>
/// DI registration for the memory index subsystem. Assumes the host has already registered
/// <see cref="AgentEnvironment"/>, <see cref="Models.Configs.AgentConfig"/>, the
/// <see cref="Func{String, ILlmTextProvider}"/> resolver, and the
/// <see cref="Func{String, IEmbeddingProvider}"/> resolver.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryIndex(this IServiceCollection services)
    {
        services.AddSingleton<MemoryChunkStore>();
        services.AddSingleton<MemoryChunker>();
        services.AddSingleton<MemoryIndexService>();
        services.AddSingleton<IToolHandler, MemoryToolHandler>();
        services.AddHostedService<MemoryIndexHostedService>();
        return services;
    }
}
