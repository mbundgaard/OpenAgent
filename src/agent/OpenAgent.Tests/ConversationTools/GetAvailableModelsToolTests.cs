using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Providers;
using OpenAgent.Tools.Conversation;

namespace OpenAgent.Tests.ConversationTools;

public class GetAvailableModelsToolTests
{
    [Fact]
    public async Task ReturnsTextAndVoiceGrouped()
    {
        var configurables = new IConfigurable[]
        {
            new FakeModelProvider("provider-a", ["model-1", "model-2"]),
            new FakeModelProvider("provider-b", ["model-3"]),
            new FakeVoiceModelProvider("voice-a", ["voice-1"])
        };
        var tool = new GetAvailableModelsTool(() => configurables);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        var text = root.GetProperty("text");
        Assert.Equal(2, text.GetArrayLength());
        Assert.Equal("provider-a", text[0].GetProperty("provider").GetString());
        Assert.Equal(2, text[0].GetProperty("models").GetArrayLength());
        Assert.Equal("provider-b", text[1].GetProperty("provider").GetString());

        var voice = root.GetProperty("voice");
        Assert.Equal(1, voice.GetArrayLength());
        Assert.Equal("voice-a", voice[0].GetProperty("provider").GetString());
        Assert.Equal("voice-1", voice[0].GetProperty("models")[0].GetString());
    }

    [Fact]
    public async Task ExcludesProvidersWithNoModels()
    {
        var configurables = new IConfigurable[]
        {
            new FakeModelProvider("configured", ["model-1"]),
            new FakeModelProvider("unconfigured", [])
        };
        var tool = new GetAvailableModelsTool(() => configurables);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);

        var text = doc.RootElement.GetProperty("text");
        Assert.Equal(1, text.GetArrayLength());
        Assert.Equal("configured", text[0].GetProperty("provider").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("voice").GetArrayLength());
    }
}
