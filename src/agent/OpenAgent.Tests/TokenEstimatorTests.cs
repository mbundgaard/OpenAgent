using OpenAgent.Compaction;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void User_message_estimates_from_content_length()
    {
        var msg = new Message { Id = "m", ConversationId = "c", Role = "user", Content = "Hello, world!" }; // 13 chars
        Assert.Equal(4, TokenEstimator.EstimateMessage(msg)); // ceil(13/4)
    }

    [Fact]
    public void Assistant_with_tool_calls_includes_tool_call_payload()
    {
        var msg = new Message
        {
            Id = "m", ConversationId = "c", Role = "assistant",
            Content = "thinking out loud",
            ToolCalls = """[{"id":"t1","function":{"name":"read","arguments":"{\"path\":\"x\"}"}}]"""
        };
        var tokens = TokenEstimator.EstimateMessage(msg);
        var minExpected = (int)Math.Ceiling((msg.Content.Length + msg.ToolCalls.Length) / 4.0);
        Assert.Equal(minExpected, tokens);
    }

    [Fact]
    public void Tool_result_uses_FullToolResult_when_present()
    {
        var shortSummary = """{"tool":"read","status":"ok","size":999999}""";
        var longFull = new string('x', 4000);
        var msg = new Message
        {
            Id = "m", ConversationId = "c", Role = "tool",
            Content = shortSummary,
            FullToolResult = longFull
        };
        Assert.Equal(1000, TokenEstimator.EstimateMessage(msg)); // 4000/4
    }

    [Fact]
    public void Tool_result_falls_back_to_Content_when_FullToolResult_is_null()
    {
        var content = new string('x', 400);
        var msg = new Message { Id = "m", ConversationId = "c", Role = "tool", Content = content };
        Assert.Equal(100, TokenEstimator.EstimateMessage(msg));
    }

    [Fact]
    public void Tool_result_is_capped_to_prevent_single_message_domination()
    {
        var huge = new string('x', 1_000_000);
        var msg = new Message { Id = "m", ConversationId = "c", Role = "tool", FullToolResult = huge };
        Assert.Equal(TokenEstimator.ToolResultTokenCap, TokenEstimator.EstimateMessage(msg));
    }

    [Fact]
    public void Null_content_returns_zero()
    {
        var msg = new Message { Id = "m", ConversationId = "c", Role = "user", Content = null };
        Assert.Equal(0, TokenEstimator.EstimateMessage(msg));
    }
}
