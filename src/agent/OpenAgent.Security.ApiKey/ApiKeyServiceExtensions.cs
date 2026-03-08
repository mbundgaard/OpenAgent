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
    /// Adds API key authentication using the "Authentication:ApiKey" config value.
    /// </summary>
    public static IServiceCollection AddApiKeyAuth(this IServiceCollection services, IConfiguration configuration)
    {
        // Read the API key from configuration — fail fast if missing
        var apiKey = configuration["Authentication:ApiKey"]
            ?? throw new InvalidOperationException("Authentication:ApiKey is not configured.");

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
}
