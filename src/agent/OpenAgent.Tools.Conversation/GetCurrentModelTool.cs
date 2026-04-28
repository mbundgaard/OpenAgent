using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Returns the active LLM provider and model for the current conversation.
/// The kind parameter selects 'text' or 'voice'; omit to get both pairs.
/// </summary>
public sealed class GetCurrentModelTool(IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_current_model",
        Description = "Get the active LLM provider and model for the current conversation. Pass kind='text' or kind='voice' to query a specific modality, or omit kind to receive both pairs.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                kind = new
                {
                    type = "string",
                    @enum = new[] { "text", "voice" },
                    description = "Optional modality filter. Omit to return both."
                }
            },
            required = Array.Empty<string>()
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Conversation '{conversationId}' not found." }));

        string? kind = null;
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            if (args.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
                kind = k.GetString();
        }

        return kind switch
        {
            "text" => Task.FromResult(JsonSerializer.Serialize(new
            {
                kind,
                provider = conversation.TextProvider,
                model = conversation.TextModel
            })),
            "voice" => Task.FromResult(JsonSerializer.Serialize(new
            {
                kind,
                provider = conversation.VoiceProvider,
                model = conversation.VoiceModel
            })),
            _ => Task.FromResult(JsonSerializer.Serialize(new
            {
                text = new { provider = conversation.TextProvider, model = conversation.TextModel },
                voice = new { provider = conversation.VoiceProvider, model = conversation.VoiceModel }
            }))
        };
    }
}
