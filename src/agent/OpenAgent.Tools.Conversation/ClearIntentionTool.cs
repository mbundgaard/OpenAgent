using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Removes the conversation intention, allowing the agent to respond without a scoped topic.
/// </summary>
public sealed class ClearIntentionTool(IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "clear_intention",
        Description = "Remove the conversation intention, allowing the agent to respond without a scoped topic.",
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
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Conversation not found" }));

        if (conversation.Intention is null)
            return Task.FromResult(JsonSerializer.Serialize(new { status = "not_set" }));

        var previous = conversation.Intention;
        conversation.Intention = null;
        store.Update(conversation);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "cleared",
            previous_intention = previous
        }));
    }
}
