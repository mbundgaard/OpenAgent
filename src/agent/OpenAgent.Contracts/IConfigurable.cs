using OpenAgent.Models.Providers;
using System.Text.Json;

namespace OpenAgent.Contracts;

/// <summary>
/// Marks a provider as runtime-configurable. Exposes its required configuration fields
/// and accepts a JSON payload to apply them.
/// </summary>
public interface IConfigurable
{
    /// <summary>Unique key used to identify this provider in config storage and admin endpoints.</summary>
    string Key { get; }

    /// <summary>Describes the configuration fields this provider accepts.</summary>
    IReadOnlyList<ProviderConfigField> ConfigFields { get; }

    /// <summary>
    /// Normalizes or enriches a configuration payload before it is applied and persisted.
    /// Default behavior returns the payload unchanged.
    /// </summary>
    ValueTask<JsonElement> NormalizeConfigAsync(JsonElement configuration, CancellationToken ct = default)
        => ValueTask.FromResult(configuration);

    /// <summary>Applies configuration from a JSON payload. Throws on invalid or missing values.</summary>
    void Configure(JsonElement configuration);

    /// <summary>Available models/deployments for this provider. Empty if not a model provider.</summary>
    IReadOnlyList<string> Models => [];

    /// <summary>
    /// Names of stored fields that should be masked in admin-facing responses but are NOT
    /// part of the user-facing <see cref="ConfigFields"/> form. Use for internal credentials
    /// like rotated refresh tokens — the field is persisted by the provider's normalization
    /// step, but should never leave the server in plaintext via the values endpoint.
    /// </summary>
    IReadOnlyCollection<string> InternalSecretKeys => [];
}