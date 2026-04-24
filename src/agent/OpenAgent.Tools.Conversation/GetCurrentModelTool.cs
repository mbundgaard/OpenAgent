using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Returns the active provider and model for the current conversation.
/// </summary>
public sealed class GetCurrentModelTool(IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_current_model",
        Description = "Get the active text LLM provider and model for the current conversation.",
        Parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Conversation '{conversationId}' not found." }));

        var result = new { provider = conversation.Provider, model = conversation.Model };
        return Task.FromResult(JsonSerializer.Serialize(result));
    }
}
