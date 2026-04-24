using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.Conversation;

/// <summary>
/// Sets the mention-name filter on the conversation. When set, inbound user
/// messages that do not contain any of the listed names (case-insensitive
/// substring match) are silently dropped before reaching the LLM. Useful
/// when the conversation lives in a busy group chat and the agent should
/// only respond when explicitly addressed.
/// </summary>
public sealed class SetMentionNamesTool(IConversationStore store) : ITool
{
    private const int MaxNames = 20;
    private const int MaxNameLength = 50;

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_mention_names",
        Description = "Enable mention-filter mode on this conversation. Only incoming user messages that contain at least one of the listed names (case-insensitive substring) will be processed; all other messages are dropped silently. Use when the user asks you to 'only reply when mentioned', or when you've been added to a noisy group chat. Replaces any existing mention-name list. Call clear_mention_names to go back to replying to every message.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                names = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Trigger names to watch for in incoming messages. Typically the agent's name and any short nicknames (e.g. ['Dex', 'fox']). Matched case-insensitively as a substring."
                }
            },
            required = new[] { "names" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;

        if (!args.TryGetProperty("names", out var namesElement) || namesElement.ValueKind != JsonValueKind.Array)
            return Task.FromResult(JsonSerializer.Serialize(new { error = "'names' must be an array of strings." }));

        var cleaned = new List<string>();
        foreach (var item in namesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = item.GetString()?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Length > MaxNameLength)
                return Task.FromResult(JsonSerializer.Serialize(new { error = $"Name '{value[..Math.Min(20, value.Length)]}...' exceeds max length of {MaxNameLength}." }));
            cleaned.Add(value);
        }

        if (cleaned.Count == 0)
            return Task.FromResult(JsonSerializer.Serialize(new { error = "At least one non-empty name is required. Use clear_mention_names to disable the filter." }));

        if (cleaned.Count > MaxNames)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Too many names ({cleaned.Count}, max {MaxNames})." }));

        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Conversation not found" }));

        var previous = conversation.MentionNames;
        conversation.MentionNames = cleaned;
        store.Update(conversation);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "set",
            mention_names = cleaned,
            previous_mention_names = previous,
            message = "Mention filter enabled. Only messages containing one of these names will be processed."
        }));
    }
}
