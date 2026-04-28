using System.Text.Json;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;
using OpenAgent.Tools.Conversation;

namespace OpenAgent.Tests.ConversationTools;

public class GetCurrentModelToolTests
{
    [Fact]
    public async Task ReturnsBothPairsByDefault()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv-1", "app",
            "anthropic-subscription", "claude-sonnet-4-6",
            "azure-openai-voice", "gpt-realtime");
        var tool = new GetCurrentModelTool(store);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("anthropic-subscription", doc.RootElement.GetProperty("text").GetProperty("provider").GetString());
        Assert.Equal("claude-sonnet-4-6", doc.RootElement.GetProperty("text").GetProperty("model").GetString());
        Assert.Equal("azure-openai-voice", doc.RootElement.GetProperty("voice").GetProperty("provider").GetString());
        Assert.Equal("gpt-realtime", doc.RootElement.GetProperty("voice").GetProperty("model").GetString());
    }

    [Fact]
    public async Task FiltersToTextWhenKindIsText()
    {
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv-1", "app",
            "anthropic-subscription", "claude-sonnet-4-6",
            "azure-openai-voice", "gpt-realtime");
        var tool = new GetCurrentModelTool(store);

        var result = await tool.ExecuteAsync("{\"kind\":\"text\"}", "conv-1");
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
