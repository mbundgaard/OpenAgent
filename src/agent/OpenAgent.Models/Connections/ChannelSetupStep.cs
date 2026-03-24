using System.Text.Json.Serialization;

namespace OpenAgent.Models.Connections;

/// <summary>
/// Describes a post-creation setup step required for a channel type.
/// </summary>
public sealed record ChannelSetupStep
{
    /// <summary>Setup step type: "qr-code", "none", or future types like "oauth".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Relative URL pattern for the setup endpoint. {id} is replaced with the connection ID.</summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }
}
