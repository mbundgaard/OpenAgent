using System.Text.Json.Serialization;
using OpenAgent.Models.Providers;

namespace OpenAgent.Models.Connections;

/// <summary>
/// Describes a channel type and its configuration requirements, used by the frontend to render dynamic forms.
/// </summary>
public sealed record ChannelTypeInfo
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("configFields")]
    public required IReadOnlyList<ProviderConfigField> ConfigFields { get; init; }

    [JsonPropertyName("setupStep")]
    public ChannelSetupStep? SetupStep { get; init; }
}
