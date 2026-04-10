namespace OpenAgent.LlmVoice.GrokRealtime.Models;

/// <summary>
/// Connection settings for the Grok Realtime API (API key, model, voice).
/// </summary>
public sealed class GrokConfig
{
    public string ApiKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Models { get; set; } = [];
    public string? Voice { get; set; }
}
