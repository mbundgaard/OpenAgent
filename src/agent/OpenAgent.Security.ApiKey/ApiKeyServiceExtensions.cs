using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OpenAgent.Security.ApiKey;

/// <summary>
/// Registers API key authentication and authorization.
/// Swap this call for AddEntraIdAuth() when migrating to Entra ID.
/// </summary>
public static class ApiKeyServiceExtensions
{
    public const string SchemeName = "ApiKey";

    /// <summary>
    /// Adds API key authentication using a resolved API key value.
    /// Use <see cref="ApiKeyResolver.Resolve"/> to obtain the key from agent.json with env-var override.
    /// </summary>
    public static IServiceCollection AddApiKeyAuth(this IServiceCollection services, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be empty.", nameof(apiKey));

        // Register the custom API key authentication scheme
        services.AddAuthentication(SchemeName)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(SchemeName, options =>
            {
                options.ApiKey = apiKey;
            });

        // Enable authorization middleware
        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Adds API key authentication using the "Authentication:ApiKey" config value.
    /// Kept for compatibility with callers that pre-resolve via configuration only.
    /// New code should use <see cref="ApiKeyResolver.Resolve"/> + the string overload.
    /// </summary>
    public static IServiceCollection AddApiKeyAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var apiKey = configuration["Authentication:ApiKey"]
            ?? throw new InvalidOperationException("Authentication:ApiKey is not configured.");
        return AddApiKeyAuth(services, apiKey);
    }
}
