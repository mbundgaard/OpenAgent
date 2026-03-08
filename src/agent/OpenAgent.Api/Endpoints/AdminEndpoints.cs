using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Admin endpoints for managing provider configurations at runtime.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    /// Maps provider configuration endpoints under /api/admin/providers.
    /// </summary>
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/providers").RequireAuthorization();

        group.MapGet("/", (IEnumerable<IConfigurable> configurables) =>
        {
            return Results.Ok(configurables.Select(c => c.Key));
        });

        group.MapGet("/{key}/config", (string key, IEnumerable<IConfigurable> configurables) =>
        {
            var configurable = configurables.FirstOrDefault(c => c.Key == key);
            if (configurable is null)
                return Results.NotFound();

            return Results.Ok(configurable.ConfigFields);
        });

        group.MapPost("/{key}/config", (string key, JsonElement config,
            IEnumerable<IConfigurable> configurables, IConfigStore configStore) =>
        {
            var configurable = configurables.FirstOrDefault(c => c.Key == key);
            if (configurable is null)
                return Results.NotFound();

            configurable.Configure(config);
            configStore.Save(key, config);

            return Results.NoContent();
        });
    }
}
