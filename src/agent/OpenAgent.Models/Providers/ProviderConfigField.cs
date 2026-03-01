namespace OpenAgent.Models.Providers;

/// <summary>
/// Describes one configurable field on a provider — its key, display label, type, and constraints.
/// Used by the UI to render dynamic configuration forms.
/// </summary>
public sealed record ProviderConfigField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Type { get; init; }
    public bool Required { get; init; }
    public string? DefaultValue { get; init; }
    public string[]? Options { get; init; } // for Enum type
}
