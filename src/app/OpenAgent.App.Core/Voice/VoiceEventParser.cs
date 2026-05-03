using System.Text.Json;

namespace OpenAgent.App.Core.Voice;

/// <summary>Parses voice WebSocket text-frame JSON payloads into the <see cref="VoiceEvent"/> closed union.</summary>
public static class VoiceEventParser
{
    /// <summary>Parses a single JSON text frame. Returns null for unknown event types.</summary>
    public static VoiceEvent? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return null;
        var type = typeProp.GetString();
        var root = doc.RootElement;

        return type switch
        {
            "session_ready" => new VoiceEvent.SessionReady(
                root.GetProperty("input_sample_rate").GetInt32(),
                root.GetProperty("output_sample_rate").GetInt32(),
                root.GetProperty("input_codec").GetString() ?? "",
                root.GetProperty("output_codec").GetString() ?? ""),
            "speech_started" => new VoiceEvent.SpeechStarted(),
            "speech_stopped" => new VoiceEvent.SpeechStopped(),
            "audio_done" => new VoiceEvent.AudioDone(),
            "thinking_started" => new VoiceEvent.ThinkingStarted(),
            "thinking_stopped" => new VoiceEvent.ThinkingStopped(),
            "error" => new VoiceEvent.Error(root.GetProperty("message").GetString() ?? ""),
            _ => null
        };
    }
}
