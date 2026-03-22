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

    /// <summary>Applies configuration from a JSON payload. Throws on invalid or missing values.</summary>
    void Configure(JsonElement configuration);

    /// <summary>Available models/deployments for this provider. Empty if not a model provider.</summary>
    IReadOnlyList<string> Models => [];
}