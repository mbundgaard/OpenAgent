using System.Text.Json;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;
using OpenAgent.Tools.Conversation;
using OpenAgent.Contracts;

namespace OpenAgent.Tests.ConversationTools;

public class SetModelToolTests
{
    private readonly InMemoryConversationStore _store = new();
    private readonly ILlmTextProvider[] _providers =
    [
        new FakeModelProvider("provider-a", ["model-1", "model-2"]),
        new FakeModelProvider("provider-b", ["model-3"])
    ];

    [Fact]
    public async Task UpdatesConversationProviderAndModel()
    {
        _store.GetOrCreate("conv-1", "app", ConversationType.Text, "provider-a", "model-1");
        var tool = new SetModelTool(_store, () => _providers);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { provider = "provider-b", model = "model-3" }), "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal("provider-a", doc.RootElement.GetProperty("previous_provider").GetString());
        Assert.Equal("model-1", doc.RootElement.GetProperty("previous_model").GetString());
        Assert.Equal("provider-b", doc.RootElement.GetProperty("provider").GetString());
        Assert.Equal("model-3", doc.RootElement.GetProperty("model").GetString());

        var conversation = _store.Get("conv-1")!;
        Assert.Equal("provider-b", conversation.Provider);
        Assert.Equal("model-3", conversation.Model);
    }

    [Fact]
    public async Task RejectsUnknownProvider()
    {
        _store.GetOrCreate("conv-1", "app", ConversationType.Text, "provider-a", "model-1");
        var tool = new SetModelTool(_store, () => _providers);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { provider = "unknown", model = "model-1" }), "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Contains("unknown", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.TryGetProperty("available_providers", out _));

        var conversation = _store.Get("conv-1")!;
        Assert.Equal("provider-a", conversation.Provider);
        Assert.Equal("model-1", conversation.Model);
    }

    [Fact]
    public async Task RejectsUnknownModel()
    {
        _store.GetOrCreate("conv-1", "app", ConversationType.Text, "provider-a", "model-1");
        var tool = new SetModelTool(_store, () => _providers);

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { provider = "provider-a", model = "nonexistent" }), "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Contains("nonexistent", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.TryGetProperty("available_models", out _));

        var conversation = _store.Get("conv-1")!;
        Assert.Equal("provider-a", conversation.Provider);
        Assert.Equal("model-1", conversation.Model);
    }

    [Fact]
    public async Task DoesNotAffectOtherConversations()
    {
        _store.GetOrCreate("conv-1", "app", ConversationType.Text, "provider-a", "model-1");
        _store.GetOrCreate("conv-2", "app", ConversationType.Text, "provider-a", "model-1");
        var tool = new SetModelTool(_store, () => _providers);

        await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { provider = "provider-b", model = "model-3" }), "conv-1");

        var conv2 = _store.Get("conv-2")!;
        Assert.Equal("provider-a", conv2.Provider);
        Assert.Equal("model-1", conv2.Model);
    }
}
