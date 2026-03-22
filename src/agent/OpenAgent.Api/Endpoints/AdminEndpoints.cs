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

        /// <summary>
        /// Returns the current saved configuration values for a provider.
        /// Secret fields are masked with "***".
        /// </summary>
        group.MapGet("/{key}/values", (string key, IEnumerable<IConfigurable> configurables, IConfigStore configStore) =>
        {
            var configurable = configurables.FirstOrDefault(c => c.Key == key);
            if (configurable is null)
                return Results.NotFound();

            var saved = configStore.Load(key);
            if (saved is null)
                return Results.Ok(new { configured = false });

            // Mask secret fields
            var secretKeys = configurable.ConfigFields
                .Where(f => f.Type == "Secret")
                .Select(f => f.Key)
                .ToHashSet();

            var masked = new Dictionary<string, object?> { ["configured"] = true };
            foreach (var prop in saved.Value.EnumerateObject())
            {
                masked[prop.Name] = secretKeys.Contains(prop.Name)
                    ? "***"
                    : prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();
            }

            return Results.Ok(masked);
        });

        group.MapPost("/{key}/config", (string key, JsonElement config,
            IEnumerable<IConfigurable> configurables, IConfigStore configStore) =>
        {
            var configurable = configurables.FirstOrDefault(c => c.Key == key);
            if (configurable is null)
                return Results.NotFound();

            // Merge incoming config with existing saved config so partial updates work
            // (e.g. changing voice without re-sending the apiKey)
            var existing = configStore.Load(key);
            JsonElement merged;
            if (existing.HasValue)
            {
                var dict = new Dictionary<string, JsonElement>();
                foreach (var prop in existing.Value.EnumerateObject())
                    dict[prop.Name] = prop.Value;
                foreach (var prop in config.EnumerateObject())
                    dict[prop.Name] = prop.Value;

                merged = JsonSerializer.SerializeToElement(dict);
            }
            else
            {
                merged = config;
            }

            configurable.Configure(merged);
            configStore.Save(key, merged);

            return Results.NoContent();
        });
    }
}
