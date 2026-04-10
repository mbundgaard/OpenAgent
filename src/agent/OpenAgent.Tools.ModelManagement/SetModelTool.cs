using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.ModelManagement;

/// <summary>
/// Changes the text LLM provider and model for the current conversation.
/// Takes effect on the next LLM call.
/// </summary>
public sealed class SetModelTool(IConversationStore store, IEnumerable<ILlmTextProvider> providers) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_model",
        Description = "Change the text LLM provider and model for the current conversation. Takes effect on the next message.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                provider = new { type = "string", description = "Provider key (e.g. 'anthropic-subscription')" },
                model = new { type = "string", description = "Model name (e.g. 'claude-sonnet-4-6')" }
            },
            required = new[] { "provider", "model" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var providerKey = args.GetProperty("provider").GetString()!;
        var modelName = args.GetProperty("model").GetString()!;

        // Validate provider exists
        var providerList = providers.ToList();
        var targetProvider = providerList.FirstOrDefault(p => p.Key == providerKey);
        if (targetProvider is null)
        {
            var available = providerList.Select(p => p.Key).ToList();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                error = $"Unknown provider '{providerKey}'.",
                available_providers = available
            }));
        }

        // Validate model exists for that provider
        if (!targetProvider.Models.Contains(modelName))
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                error = $"Unknown model '{modelName}' for provider '{providerKey}'.",
                available_models = targetProvider.Models
            }));
        }

        // Load conversation and update
        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Conversation '{conversationId}' not found." }));

        var previousProvider = conversation.Provider;
        var previousModel = conversation.Model;

        conversation.Provider = providerKey;
        conversation.Model = modelName;
        store.Update(conversation);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            previous_provider = previousProvider,
            previous_model = previousModel,
            provider = providerKey,
            model = modelName
        }));
    }
}
