using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Removes the mention filter, restoring the conversation to reply-to-all.
/// </summary>
public sealed class ClearMentionFilterTool(IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "clear_mention_filter",
        Description = "Disable the mention filter, restoring the conversation to reply-to-all. Use when the user asks you to 'respond to everything again' or when you were previously limited to mentions and that restriction should be lifted.",
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

        if (conversation.MentionFilter is null || conversation.MentionFilter.Count == 0)
            return Task.FromResult(JsonSerializer.Serialize(new { status = "not_set" }));

        var previous = conversation.MentionFilter;
        conversation.MentionFilter = null;
        store.Update(conversation);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "cleared",
            previous_mention_filter = previous
        }));
    }
}
