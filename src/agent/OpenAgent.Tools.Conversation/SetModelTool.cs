using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Changes the LLM provider and model for the current conversation.
/// Each conversation carries separate text and voice provider/model pairs;
/// the kind parameter selects which pair to update. Takes effect on the
/// next LLM call (text or voice respectively).
/// </summary>
public sealed class SetModelTool(IConversationStore store, Func<IEnumerable<IConfigurable>> resolveAllConfigurables) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_model",
        Description = "Change the LLM provider and model for the current conversation. The kind parameter selects 'text' or 'voice'. Takes effect on the next message in that modality.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                kind = new
                {
                    type = "string",
                    @enum = new[] { "text", "voice" },
                    description = "Which modality to update — 'text' for chat / REST / channel turns, 'voice' for realtime voice sessions"
                },
                provider = new { type = "string", description = "Provider key (e.g. 'anthropic-subscription', 'azure-openai-voice')" },
                model = new { type = "string", description = "Model name (e.g. 'claude-sonnet-4-6', 'gpt-realtime')" }
            },
            required = new[] { "kind", "provider", "model" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var kind = args.GetProperty("kind").GetString()!;
        var providerKey = args.GetProperty("provider").GetString()!;
        var modelName = args.GetProperty("model").GetString()!;

        if (kind != "text" && kind != "voice")
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                error = $"Unknown kind '{kind}'. Expected 'text' or 'voice'."
            }));

        // Resolve configurables of the right capability and look up the named provider.
        // ILlmTextProvider and ILlmVoiceProvider both implement IConfigurable, which is
        // what carries Models — that's why we filter on capability here.
        var allConfigurables = resolveAllConfigurables().ToList();
        var candidates = kind == "text"
            ? allConfigurables.OfType<ILlmTextProvider>().Cast<IConfigurable>().ToList()
            : allConfigurables.OfType<ILlmVoiceProvider>().Cast<IConfigurable>().ToList();

        var targetProvider = candidates.FirstOrDefault(p => p.Key == providerKey);
        if (targetProvider is null)
        {
            var available = candidates.Select(p => p.Key).ToList();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                error = $"Unknown {kind} provider '{providerKey}'.",
                available_providers = available
            }));
        }

        if (targetProvider.Models.Count > 0 && !targetProvider.Models.Contains(modelName))
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                error = $"Unknown model '{modelName}' for {kind} provider '{providerKey}'.",
                available_models = targetProvider.Models
            }));
        }

        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Conversation '{conversationId}' not found." }));

        if (kind == "text")
        {
            var previousProvider = conversation.TextProvider;
            var previousModel = conversation.TextModel;
            conversation.TextProvider = providerKey;
            conversation.TextModel = modelName;
            store.Update(conversation);

            return Task.FromResult(JsonSerializer.Serialize(new
            {
                kind,
                previous_provider = previousProvider,
                previous_model = previousModel,
                provider = providerKey,
                model = modelName
            }));
        }
        else
        {
            var previousProvider = conversation.VoiceProvider;
            var previousModel = conversation.VoiceModel;
            conversation.VoiceProvider = providerKey;
            conversation.VoiceModel = modelName;
            store.Update(conversation);

            return Task.FromResult(JsonSerializer.Serialize(new
            {
                kind,
                previous_provider = previousProvider,
                previous_model = previousModel,
                provider = providerKey,
                model = modelName
            }));
        }
    }
}
