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

    /// <summary>
    /// Indexer threshold: the N most recent daily memory files stay on disk in memory/ root.
    /// Everything older is a candidate for the nightly memory-index job, which chunks, embeds,
    /// and moves each file to memory/backup/. The system prompt loads whatever files are still
    /// in memory/ root at build time, so this setting implicitly controls prompt size too —
    /// but the prompt loader itself doesn't read MemoryDays; it just reflects what the indexer
    /// has left.
    /// </summary>
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
    /// Embedding model within the chosen provider (e.g. "multilingual-e5-base" for the onnx
    /// provider). Default assumes the middle-sized e5 variant. Changing this requires a
    /// restart — the provider loads its model files on first use.
    /// </summary>
    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; set; } = "multilingual-e5-base";
}
