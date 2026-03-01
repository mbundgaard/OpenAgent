namespace OpenAgent.Models.Configs;

/// <summary>
/// Application-wide settings such as the default voice provider selection.
/// </summary>
public sealed class GlobalConfig
{
    public string? DefaultVoiceProvider { get; set; }
}
