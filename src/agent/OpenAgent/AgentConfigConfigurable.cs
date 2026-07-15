using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Providers;

namespace OpenAgent;

/// <summary>
/// IConfigurable wrapper for AgentConfig — exposes it via the admin API.
/// Lives in the host project because IConfigurable requires ProviderConfigField.
/// </summary>
public sealed class AgentConfigConfigurable(AgentConfig agentConfig) : IConfigurable
{
    public string Key => AgentConfig.ConfigKey;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "textProvider", Label = "Text Provider", Type = "String", Required = true },
        new() { Key = "textModel", Label = "Text Model", Type = "String", Required = true },
        new() { Key = "voiceProvider", Label = "Voice Provider", Type = "String", Required = true },
        new() { Key = "voiceModel", Label = "Voice Model", Type = "String", Required = true },
        new() { Key = "compactionProvider", Label = "Compaction Provider", Type = "String", Required = true },
        new() { Key = "compactionModel", Label = "Compaction Model", Type = "String", Required = true },
        new() { Key = "memoryDays", Label = "Memory Days", Type = "String", Required = false },
        new() { Key = "mainConversationId", Label = "Main Conversation", Type = "String", Required = false },
        new() { Key = "embeddingProvider", Label = "Embedding Provider", Type = "String", Required = false },
        new() { Key = "embeddingModel", Label = "Embedding Model", Type = "String", Required = false },
        new() { Key = "maxToolRounds", Label = "Max Tool Rounds", Type = "String", Required = false },
        new() { Key = "backgroundAgentEnabled", Label = "Background Agent Enabled", Type = "Enum", Required = false, DefaultValue = "false", Options = ["false", "true"] },
        new() { Key = "backgroundAgentProvider", Label = "Background Agent Provider", Type = "String", Required = false },
        new() { Key = "backgroundAgentModel", Label = "Background Agent Model", Type = "String", Required = false }
    ];

    public void Configure(JsonElement configuration)
    {
        if (configuration.TryGetProperty("textProvider", out var tp))
            agentConfig.TextProvider = tp.GetString()!;
        if (configuration.TryGetProperty("textModel", out var tm))
            agentConfig.TextModel = tm.GetString()!;
        if (configuration.TryGetProperty("voiceProvider", out var vp))
            agentConfig.VoiceProvider = vp.GetString()!;
        if (configuration.TryGetProperty("voiceModel", out var vm))
            agentConfig.VoiceModel = vm.GetString()!;
        if (configuration.TryGetProperty("compactionProvider", out var cp))
            agentConfig.CompactionProvider = cp.GetString()!;
        if (configuration.TryGetProperty("compactionModel", out var cm))
            agentConfig.CompactionModel = cm.GetString()!;
        if (configuration.TryGetProperty("memoryDays", out var md))
        {
            // Accept both string and number — the admin UI sends strings
            if (md.ValueKind == JsonValueKind.String && int.TryParse(md.GetString(), out var mdInt))
                agentConfig.MemoryDays = mdInt;
            else if (md.ValueKind == JsonValueKind.Number)
                agentConfig.MemoryDays = md.GetInt32();
        }
        if (configuration.TryGetProperty("mainConversationId", out var mc))
        {
            var value = mc.GetString();
            agentConfig.MainConversationId = string.IsNullOrEmpty(value) ? null : value;
        }
        if (configuration.TryGetProperty("embeddingProvider", out var ep))
            agentConfig.EmbeddingProvider = ep.GetString() ?? "";
        if (configuration.TryGetProperty("embeddingModel", out var em))
        {
            var value = em.GetString();
            if (!string.IsNullOrEmpty(value))
                agentConfig.EmbeddingModel = value;
        }
        if (configuration.TryGetProperty("maxToolRounds", out var mtr))
        {
            // Accept both string and number — the admin UI sends strings
            if (mtr.ValueKind == JsonValueKind.String && int.TryParse(mtr.GetString(), out var mtrInt))
                agentConfig.MaxToolRounds = mtrInt;
            else if (mtr.ValueKind == JsonValueKind.Number)
                agentConfig.MaxToolRounds = mtr.GetInt32();
        }
        if (configuration.TryGetProperty("backgroundAgentEnabled", out var bae))
        {
            // Accept both a JSON bool and a "true"/"false" string — the admin UI sends strings
            if (bae.ValueKind is JsonValueKind.True or JsonValueKind.False)
                agentConfig.BackgroundAgentEnabled = bae.GetBoolean();
            else if (bae.ValueKind == JsonValueKind.String && bool.TryParse(bae.GetString(), out var baeBool))
                agentConfig.BackgroundAgentEnabled = baeBool;
        }
        if (configuration.TryGetProperty("backgroundAgentProvider", out var bap))
            agentConfig.BackgroundAgentProvider = bap.GetString() ?? "";
        if (configuration.TryGetProperty("backgroundAgentModel", out var bam))
            agentConfig.BackgroundAgentModel = bam.GetString() ?? "";
    }
}
