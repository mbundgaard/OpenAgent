using System.Text.Json.Serialization;

namespace OpenAgent.Models.Providers;

/// <summary>
/// Describes one configurable field on a provider — its key, display label, type, and constraints.
/// Used by the UI to render dynamic configuration forms.
/// </summary>
public sealed record ProviderConfigField
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }
    [JsonPropertyName("label")]
    public required string Label { get; init; }
    [JsonPropertyName("type")]
    public required string Type { get; init; }
    [JsonPropertyName("required")]
    public bool Required { get; init; }
    [JsonPropertyName("default_value")]
    public string? DefaultValue { get; init; }
    [JsonPropertyName("options")]
    public string[]? Options { get; init; } // for Enum type
}
