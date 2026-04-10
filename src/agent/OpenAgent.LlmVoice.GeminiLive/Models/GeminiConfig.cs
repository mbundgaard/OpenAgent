namespace OpenAgent.LlmVoice.GeminiLive.Models;

/// <summary>
/// Connection settings for the Gemini Live API.
/// </summary>
public sealed class GeminiConfig
{
    public string ApiKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Models { get; set; } = [];
    public string? Voice { get; set; }
    /// <summary>Proactive reconnect threshold in minutes. Default 13 (session cap is ~15).</summary>
    public int ReconnectAfterMinutes { get; set; } = 13;
}
