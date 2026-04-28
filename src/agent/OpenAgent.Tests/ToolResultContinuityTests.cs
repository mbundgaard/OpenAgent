using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Verifies the core PR 1 goal: a tool result persisted with FullToolResult on turn N
/// is available again on turn N+1 via includeToolResultBlobs, while the default read path
/// still exposes only the compact summary for the UI.
/// </summary>
public class ToolResultContinuityTests
{
    [Fact]
    public void Full_tool_result_survives_across_turns_via_store()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv1", "test", "p", "m", "p", "m");

        // Turn 1: tool result persisted with full content
        const string fullResult = "Line 1\nLine 2\nLine 3\n(this is the full tool output)";
        store.AddMessage("conv1", new Message
        {
            Id = "tm1",
            ConversationId = "conv1",
            Role = "tool",
            Content = """{"tool":"read","status":"ok","size":48}""",
            FullToolResult = fullResult,
            ToolCallId = "call_1"
        });

        // Turn 2 (LLM-facing path): full content is restored from the blob
        var messages = store.GetMessages("conv1", includeToolResultBlobs: true);
        var toolMsg = messages.Single(m => m.Id == "tm1");
        Assert.Equal(fullResult, toolMsg.FullToolResult);
        Assert.Equal("tool-results/tm1.txt", toolMsg.ToolResultRef);

        // UI-facing path: still only the compact summary, no full content
        var uiMessages = store.GetMessages("conv1");
        var uiToolMsg = uiMessages.Single(m => m.Id == "tm1");
        Assert.Null(uiToolMsg.FullToolResult);
        Assert.Equal("""{"tool":"read","status":"ok","size":48}""", uiToolMsg.Content);
    }

    [Fact]
    public void GetMessagesByIds_with_blobs_returns_full_content()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv1", "test", "p", "m", "p", "m");

        const string fullResult = "complete tool payload";
        store.AddMessage("conv1", new Message
        {
            Id = "tm1",
            ConversationId = "conv1",
            Role = "tool",
            Content = "summary",
            FullToolResult = fullResult,
            ToolCallId = "call_1"
        });

        var withBlobs = store.GetMessagesByIds(["tm1"], includeToolResultBlobs: true);
        Assert.Equal(fullResult, withBlobs.Single().FullToolResult);

        var plain = store.GetMessagesByIds(["tm1"]);
        Assert.Null(plain.Single().FullToolResult);
    }
}
