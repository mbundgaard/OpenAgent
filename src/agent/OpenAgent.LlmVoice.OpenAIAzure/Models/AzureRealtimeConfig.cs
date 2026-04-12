namespace OpenAgent.LlmVoice.OpenAIAzure.Models;

/// <summary>
/// Connection settings for the Azure OpenAI Realtime API (API key, resource, deployment, version).
/// </summary>
public sealed class AzureRealtimeConfig
{
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Models { get; set; } = [];
    public string ApiVersion { get; set; } = "2025-04-01-preview";
    public string? Voice { get; set; }

    /// <summary>
    /// Audio codec for both input and output. OpenAI Realtime wire values:
    /// "pcm16" (24 kHz), "g711_ulaw" (8 kHz), "g711_alaw" (8 kHz).
    /// </summary>
    public string? Codec { get; set; }
}
