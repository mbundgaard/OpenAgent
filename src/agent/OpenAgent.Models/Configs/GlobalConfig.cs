using System.Text.Json.Serialization;

namespace OpenAgent.Models.Configs;

/// <summary>
/// Application-wide settings such as the default voice provider selection.
/// </summary>
public sealed class GlobalConfig
{
    [JsonPropertyName("default_voice_provider")]
    public string? DefaultVoiceProvider { get; set; }
}
