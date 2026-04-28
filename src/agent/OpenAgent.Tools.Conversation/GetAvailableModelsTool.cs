using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Returns all available text and voice LLM models grouped by provider.
/// Used by the agent to discover what set_model accepts for each modality.
/// </summary>
public sealed class GetAvailableModelsTool(Func<IEnumerable<IConfigurable>> resolveAllConfigurables) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_available_models",
        Description = "List all available LLM models grouped by modality (text/voice) and provider.",
        Parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var configurables = resolveAllConfigurables().ToList();

        var text = configurables.OfType<ILlmTextProvider>()
            .Where(p => p.Models.Count > 0)
            .Select(p => new { provider = p.Key, models = p.Models })
            .ToList();

        var voice = configurables.OfType<ILlmVoiceProvider>()
            .Where(p => p.Models.Count > 0)
            .Select(p => new { provider = p.Key, models = p.Models })
            .ToList();

        return Task.FromResult(JsonSerializer.Serialize(new { text, voice }));
    }
}
