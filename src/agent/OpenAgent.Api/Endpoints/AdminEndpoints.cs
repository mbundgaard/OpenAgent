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
        app.MapGet("/api/admin/providers/openai-subscription/callback", () =>
        {
            var html = """
                       <!doctype html>
                       <html><body style="font-family:sans-serif;padding:16px;">
                         <h3>OpenAI login callback received</h3>
                         <p>Copy this full URL and paste it into the <b>Callback URL</b> field in OpenAgent settings, then click Save.</p>
                         <textarea style="width:100%;height:110px;" readonly></textarea>
                         <script>document.querySelector('textarea').value = window.location.href;</script>
                       </body></html>
                       """;
            return Results.Content(html, "text/html");
        }).AllowAnonymous();

        var group = app.MapGroup("/api/admin/providers").RequireAuthorization();

        group.MapGet("/", (IEnumerable<IConfigurable> configurables) =>
        {
            return Results.Ok(configurables.Select(c => new
            {
                key = c.Key,
                capabilities = InferCapabilities(c)
            }));
        });

        group.MapGet("/{key}/config", (string key, IEnumerable<IConfigurable> configurables) =>
        {
            var configurable = configurables.FirstOrDefault(c => c.Key == key);
            if (configurable is null)
                return Results.NotFound();

            return Results.Ok(configurable.ConfigFields);
        });

        /// <summary>
        /// Returns available models for a provider. Empty array if not a model provider.
        /// </summary>
        group.MapGet("/{key}/models", (string key, IEnumerable<IConfigurable> configurables) =>
        {
            var configurable = configurables.FirstOrDefault(c => c.Key == key);
            if (configurable is null)
                return Results.NotFound();

            return Results.Ok(configurable.Models);
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

        group.MapGet("/{key}/auth-link/{fieldKey}", (string key, string fieldKey, HttpContext http, OpenAiSubscriptionAuthService auth) =>
        {
            if (!string.Equals(key, "openai-subscription", StringComparison.Ordinal) || !string.Equals(fieldKey, "authUrl", StringComparison.Ordinal))
                return Results.NotFound();

            var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}/api/admin/providers/openai-subscription/callback";
            var url = auth.CreateAuthorizationUrl(redirectUri);
            return Results.Ok(new { url });
        });

        group.MapPost("/{key}/config", async (string key, JsonElement config,
            IEnumerable<IConfigurable> configurables, IConfigStore configStore, OpenAiSubscriptionAuthService auth, CancellationToken ct) =>
        {
            var configurable = configurables.FirstOrDefault(c => c.Key == key);
            if (configurable is null)
                return Results.NotFound();

            var dict = new Dictionary<string, JsonElement>();
            var existing = configStore.Load(key);
            if (existing.HasValue)
            {
                foreach (var prop in existing.Value.EnumerateObject())
                    dict[prop.Name] = prop.Value;
            }
            foreach (var prop in config.EnumerateObject())
                dict[prop.Name] = prop.Value;

            if (string.Equals(key, "openai-subscription", StringComparison.Ordinal)
                && dict.TryGetValue("callbackUrl", out var callbackElement)
                && callbackElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(callbackElement.GetString()))
            {
                var setupToken = await auth.ExchangeCallbackUrlAsync(callbackElement.GetString()!, ct);
                dict["setupToken"] = JsonSerializer.SerializeToElement(setupToken);
                dict["callbackUrl"] = JsonSerializer.SerializeToElement("");
            }

            // Runtime-only helper field, never persisted.
            dict.Remove("authUrl");

            var merged = JsonSerializer.SerializeToElement(dict);
            configurable.Configure(merged);
            configStore.Save(key, merged);

            return Results.NoContent();
        });
    }

    // Infers capability tags from the runtime type. Keeps the IConfigurable interface flat.
    private static string[] InferCapabilities(IConfigurable configurable)
    {
        var caps = new List<string>();
        if (configurable is ILlmTextProvider) caps.Add("text");
        if (configurable is ILlmVoiceProvider) caps.Add("voice");
        return caps.ToArray();
    }
}
