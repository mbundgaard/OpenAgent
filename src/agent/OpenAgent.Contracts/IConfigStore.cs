using System.Text.Json;

namespace OpenAgent.Contracts;

/// <summary>
/// Persists and retrieves provider configuration as JSON, keyed by provider name.
/// </summary>
public interface IConfigStore
{
    /// <summary>Loads a previously saved configuration, or null if none exists.</summary>
    JsonElement? Load(string key);

    /// <summary>Persists a configuration so it survives restarts.</summary>
    void Save(string key, JsonElement config);
}
