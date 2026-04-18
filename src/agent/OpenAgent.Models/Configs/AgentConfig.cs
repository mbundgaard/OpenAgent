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
    public string TextProvider { get; set; } = "";

    /// <summary>Default text model/deployment for new conversations.</summary>
    [JsonPropertyName("textModel")]
    public string TextModel { get; set; } = "";

    /// <summary>Default voice provider key for new conversations.</summary>
    [JsonPropertyName("voiceProvider")]
    public string VoiceProvider { get; set; } = "";

    /// <summary>Default voice model/deployment for new conversations.</summary>
    [JsonPropertyName("voiceModel")]
    public string VoiceModel { get; set; } = "";

    /// <summary>Number of recent daily memory files to include in the system prompt.</summary>
    [JsonPropertyName("memoryDays")]
    public int MemoryDays { get; set; } = 3;

    /// <summary>Provider key used for compaction summarization.</summary>
    [JsonPropertyName("compactionProvider")]
    public string CompactionProvider { get; set; } = "";

    /// <summary>Model/deployment used for compaction summarization.</summary>
    [JsonPropertyName("compactionModel")]
    public string CompactionModel { get; set; } = "";

    /// <summary>
    /// Fallback conversation for scheduled tasks created from an unbound conversation (e.g. a new
    /// web UI chat). When the agent is asked "remind me tomorrow" from a conversation with no
    /// channel binding, the task is routed here — typically the user's primary channel chat
    /// (e.g. their Telegram conversation). Null means no fallback: the tool will return an error
    /// and the agent should ask the user to configure one.
    /// </summary>
    [JsonPropertyName("mainConversationId")]
    public string? MainConversationId { get; set; }

    /// <summary>
    /// Embedding provider key used by the memory index (e.g. "onnx"). Empty disables the
    /// index entirely: the hosted service skips, search/load tools are hidden.
    /// </summary>
    [JsonPropertyName("embeddingProvider")]
    public string EmbeddingProvider { get; set; } = "";

    /// <summary>
    /// Hour of the day (Europe/Copenhagen) at which the nightly memory index job runs.
    /// </summary>
    [JsonPropertyName("indexRunAtHour")]
    public int IndexRunAtHour { get; set; } = 2;
}
