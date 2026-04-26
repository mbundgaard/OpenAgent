using OpenAgent.Tests.Fakes;
using OpenAgent.Models.Conversations;
using OpenAgent.Tools.Expand;

namespace OpenAgent.Tests;

public class ExpandToolTests
{
    [Fact]
    public async Task Expand_returns_messages_by_id()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv1", "test", "test-provider", "test-model");
        store.AddMessage("conv1", new Message { Id = "msg1", ConversationId = "conv1", Role = "user", Content = "hello" });
        store.AddMessage("conv1", new Message { Id = "msg2", ConversationId = "conv1", Role = "assistant", Content = "hi there" });
        store.AddMessage("conv1", new Message { Id = "msg3", ConversationId = "conv1", Role = "user", Content = "bye" });

        var handler = new ExpandToolHandler(store);
        var tool = handler.Tools[0];

        var result = await tool.ExecuteAsync("""{"message_ids": ["msg1", "msg3"]}""", "");

        Assert.Contains("hello", result);
        Assert.Contains("bye", result);
        Assert.DoesNotContain("hi there", result);
    }

    [Fact]
    public async Task Expand_returns_error_for_empty_ids()
    {
        var store = new InMemoryConversationStore();
        var handler = new ExpandToolHandler(store);
        var tool = handler.Tools[0];

        var result = await tool.ExecuteAsync("""{"message_ids": []}""", "");

        Assert.Contains("error", result);
    }
}
