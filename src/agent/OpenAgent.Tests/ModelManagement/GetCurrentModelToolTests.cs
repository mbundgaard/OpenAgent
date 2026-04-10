using System.Text.Json;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;
using OpenAgent.Tools.ModelManagement;

namespace OpenAgent.Tests.ModelManagement;

public class GetCurrentModelToolTests
{
    [Fact]
    public async Task ReturnsConversationProviderAndModel()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv-1", "app", ConversationType.Text, "anthropic-subscription", "claude-sonnet-4-6");
        var tool = new GetCurrentModelTool(store);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("anthropic-subscription", doc.RootElement.GetProperty("provider").GetString());
        Assert.Equal("claude-sonnet-4-6", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ReturnsErrorWhenConversationNotFound()
    {
        var store = new InMemoryConversationStore();
        var tool = new GetCurrentModelTool(store);

        var result = await tool.ExecuteAsync("{}", "nonexistent");
        var doc = JsonDocument.Parse(result);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }
}
