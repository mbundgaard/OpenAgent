using System.Text.Json.Serialization;

namespace OpenAgent.Models.Configs;

/// <summary>
/// Global agent configuration — default provider+model for text, voice, and compaction slots.
/// </summary>
public sealed class AgentConfig
{
    public const string ConfigKey = "agent";

    /// <summary>Default text provider key for new conversations.</summary>
    [JsonPropertyName("textProvider")]
    public string TextProvider { get; set; } = "azure-openai-text";

    /// <summary>Default text model/deployment for new conversations.</summary>
    [JsonPropertyName("textModel")]
    public string TextModel { get; set; } = "gpt-5.2-chat";

    /// <summary>Default voice provider key for new conversations.</summary>
    [JsonPropertyName("voiceProvider")]
    public string VoiceProvider { get; set; } = "azure-openai-voice";

    /// <summary>Default voice model/deployment for new conversations.</summary>
    [JsonPropertyName("voiceModel")]
    public string VoiceModel { get; set; } = "gpt-realtime";

    /// <summary>Provider key used for compaction summarization.</summary>
    [JsonPropertyName("compactionProvider")]
    public string CompactionProvider { get; set; } = "azure-openai-text";

    /// <summary>Model/deployment used for compaction summarization.</summary>
    [JsonPropertyName("compactionModel")]
    public string CompactionModel { get; set; } = "gpt-4.1-mini";
}
