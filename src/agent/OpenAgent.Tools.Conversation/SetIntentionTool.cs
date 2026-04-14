using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Sets the conversation intention — a short topic/purpose injected into the system
/// prompt on every turn to keep replies anchored to the topic. Replaces any existing intention.
/// </summary>
public sealed class SetIntentionTool(IConversationStore store) : ITool
{
    // ~1000 chars is plenty for a topic description — prevents abuse of the field as general memory
    private const int MaxIntentionLength = 1000;

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_intention",
        Description = "Set the scope/topic for this conversation. The intention is injected into the system prompt on every turn to keep replies anchored to the topic. Use when the user explicitly states what the conversation is for, or when you want to lock in an agreed-upon focus. Replaces any existing intention.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                intention = new { type = "string", description = "Short description of what this conversation is about (e.g. 'Planning the autumn garden layout'). Keep under 500 characters." }
            },
            required = new[] { "intention" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var intention = args.GetProperty("intention").GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(intention))
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Intention cannot be empty. Use clear_intention to remove." }));

        if (intention.Length > MaxIntentionLength)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Intention too long ({intention.Length} chars, max {MaxIntentionLength})." }));

        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Conversation not found" }));

        var previous = conversation.Intention;
        conversation.Intention = intention;
        store.Update(conversation);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "set",
            intention,
            previous_intention = previous,
            message = "Intention updated. It will appear in the system prompt on the next turn."
        }));
    }
}
