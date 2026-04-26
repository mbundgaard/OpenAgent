namespace OpenAgent.LlmVoice.GrokRealtime.Models;

/// <summary>
/// Connection settings for the Grok Realtime API (API key, voice).
/// Codec and sample rate are negotiated per session via VoiceSessionOptions.
/// </summary>
public sealed class GrokConfig
{
    public string ApiKey { get; set; } = "";
    public string? Voice { get; set; }
}
