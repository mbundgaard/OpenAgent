using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.ModelManagement;

/// <summary>
/// Returns all available models from all configured text LLM providers.
/// </summary>
public sealed class GetAvailableModelsTool(Func<IEnumerable<ILlmTextProvider>> resolveProviders) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_available_models",
        Description = "List all available text LLM models grouped by provider.",
        Parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var result = resolveProviders()
            .Where(p => p.Models.Count > 0)
            .Select(p => new { provider = p.Key, models = p.Models })
            .ToList();

        return Task.FromResult(JsonSerializer.Serialize(result));
    }
}
