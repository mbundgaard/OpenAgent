using OpenAgent.Compaction;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class CompactionCutPointTests
{
    private static Message User(string id, string content) =>
        new() { Id = id, ConversationId = "c", Role = "user", Content = content };

    private static Message Asst(string id, string content, string? toolCalls = null) =>
        new() { Id = id, ConversationId = "c", Role = "assistant", Content = content, ToolCalls = toolCalls };

    private static Message Tool(string id, string toolCallId, string full) =>
        new() { Id = id, ConversationId = "c", Role = "tool", Content = "summary", FullToolResult = full, ToolCallId = toolCallId };

    [Fact]
    public void Empty_list_returns_no_cut()
    {
        Assert.Null(CompactionCutPoint.Find([], keepRecentTokens: 100));
    }

    [Fact]
    public void Everything_fits_in_tail_returns_no_cut()
    {
        var messages = new[] { User("u1", "hi"), Asst("a1", "hello") };
        Assert.Null(CompactionCutPoint.Find(messages, keepRecentTokens: 10_000));
    }

    [Fact]
    public void Cut_snaps_to_user_message()
    {
        var big = new string('x', 400);
        var messages = new[]
        {
            User("u1", new string('x', 80)),
            Asst("a1", "ok"),
            User("u2", "next request"),
            Asst("a2", big)
        };
        var cutIndex = CompactionCutPoint.Find(messages, keepRecentTokens: 50);
        Assert.NotNull(cutIndex);
        Assert.Equal("u2", messages[cutIndex.Value].Id);
    }

    [Fact]
    public void Cut_never_lands_on_tool_result()
    {
        var messages = new[]
        {
            User("u1", "do the thing"),
            Asst("a1", "running", toolCalls: """[{"id":"t1","function":{"name":"read","arguments":"{}"}}]"""),
            Tool("tr1", "t1", new string('x', 1000)),
            Asst("a2", "done")
        };
        var cutIndex = CompactionCutPoint.Find(messages, keepRecentTokens: 100);
        Assert.NotNull(cutIndex);
        Assert.NotEqual("tool", messages[cutIndex.Value].Role);
    }

    [Fact]
    public void Cut_keeps_tool_call_and_tool_result_together()
    {
        var messages = new[]
        {
            User("u1", "old question"),
            Asst("a1", "old answer"),
            User("u2", "do tool"),
            Asst("a2", "calling tool", toolCalls: """[{"id":"t1","function":{"name":"read","arguments":"{}"}}]"""),
            Tool("tr1", "t1", "result"),
            Asst("a3", "final")
        };
        var cutIndex = CompactionCutPoint.Find(messages, keepRecentTokens: 30);
        Assert.NotNull(cutIndex);
        var tail = messages.Skip(cutIndex.Value).ToArray();
        var hasAssistantToolCall = tail.Any(m => m.Role == "assistant" && m.ToolCalls is not null);
        var hasToolResult = tail.Any(m => m.Role == "tool");
        Assert.Equal(hasAssistantToolCall, hasToolResult);
    }

    [Fact]
    public void Voice_conversation_with_short_turns_cuts_at_user_boundary()
    {
        var messages = Enumerable.Range(1, 40)
            .SelectMany<int, Message>(i => new[]
            {
                User($"u{i}", $"utterance {i}"),
                Asst($"a{i}", $"reply {i}")
            })
            .ToArray();
        var cutIndex = CompactionCutPoint.Find(messages, keepRecentTokens: 50);
        Assert.NotNull(cutIndex);
        Assert.Equal("user", messages[cutIndex.Value].Role);
    }
}
