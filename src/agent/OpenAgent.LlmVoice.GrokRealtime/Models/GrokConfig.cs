namespace OpenAgent.LlmVoice.GrokRealtime.Models;

/// <summary>
/// Connection settings for the Grok Realtime API (API key, model, voice).
/// </summary>
public sealed class GrokConfig
{
    public string ApiKey { get; set; } = "";
    public string? Voice { get; set; }

    /// <summary>
    /// Audio codec (neutral vocabulary): "pcm16", "g711_ulaw", "g711_alaw". Applied to both input and output.
    /// Defaults to "pcm16".
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// Sample rate in Hz, as a string (form field type). Applied to both input and output.
    /// Supported PCM rates: 8000, 16000, 22050, 24000, 32000, 44100, 48000.
    /// Forced to 8000 when codec is g711_ulaw / g711_alaw. Defaults to 24000 when null/blank/invalid.
    /// </summary>
    public string? SampleRate { get; set; }
}
