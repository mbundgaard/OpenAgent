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
        new() { Key = "textProvider", Label = "Text Provider", Type = "String", Required = true, DefaultValue = "azure-openai-text" },
        new() { Key = "textModel", Label = "Text Model", Type = "String", Required = true, DefaultValue = "gpt-5.2-chat" },
        new() { Key = "voiceProvider", Label = "Voice Provider", Type = "String", Required = true, DefaultValue = "azure-openai-voice" },
        new() { Key = "voiceModel", Label = "Voice Model", Type = "String", Required = true, DefaultValue = "gpt-realtime" },
        new() { Key = "compactionProvider", Label = "Compaction Provider", Type = "String", Required = true, DefaultValue = "azure-openai-text" },
        new() { Key = "compactionModel", Label = "Compaction Model", Type = "String", Required = true, DefaultValue = "gpt-4.1-mini" }
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
    }
}
