using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tools.Expand;

/// <summary>
/// Tool handler that lets the agent retrieve original messages by ID
/// from compacted conversation history.
/// </summary>
public sealed class ExpandToolHandler(IConversationStore store) : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; } = [new ExpandTool(store)];
}

internal sealed class ExpandTool(IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "expand",
        Description = "Retrieve original messages by their IDs from conversation history. Use when the conversation context summary references messages you need to see in full.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                message_ids = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "List of message IDs to retrieve (from [ref: ...] annotations in the context summary)"
                }
            },
            required = new[] { "message_ids" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<ExpandArgs>(arguments);
        if (args?.MessageIds is null or { Count: 0 })
            return Task.FromResult("""{"error": "message_ids is required"}""");

        var messages = store.GetMessagesByIds(args.MessageIds);

        var result = messages.Select(m => new
        {
            id = m.Id,
            role = m.Role,
            content = m.Content,
            created_at = m.CreatedAt.ToString("O"),
            tool_calls = m.ToolCalls,
            tool_call_id = m.ToolCallId
        });

        return Task.FromResult(JsonSerializer.Serialize(result));
    }

    private sealed class ExpandArgs
    {
        [System.Text.Json.Serialization.JsonPropertyName("message_ids")]
        public List<string>? MessageIds { get; set; }
    }
}
