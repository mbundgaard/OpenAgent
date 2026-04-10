using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Providers;
using OpenAgent.Tools.ModelManagement;

namespace OpenAgent.Tests.ModelManagement;

public class GetAvailableModelsToolTests
{
    [Fact]
    public async Task ReturnsModelsGroupedByProvider()
    {
        var providers = new ILlmTextProvider[]
        {
            new FakeModelProvider("provider-a", ["model-1", "model-2"]),
            new FakeModelProvider("provider-b", ["model-3"])
        };
        var tool = new GetAvailableModelsTool(providers);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetArrayLength());

        var first = root[0];
        Assert.Equal("provider-a", first.GetProperty("provider").GetString());
        var models = first.GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        Assert.Equal("model-1", models[0].GetString());
        Assert.Equal("model-2", models[1].GetString());

        var second = root[1];
        Assert.Equal("provider-b", second.GetProperty("provider").GetString());
        Assert.Equal(1, second.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task ExcludesProvidersWithNoModels()
    {
        var providers = new ILlmTextProvider[]
        {
            new FakeModelProvider("configured", ["model-1"]),
            new FakeModelProvider("unconfigured", [])
        };
        var tool = new GetAvailableModelsTool(providers);

        var result = await tool.ExecuteAsync("{}", "conv-1");
        var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("configured", doc.RootElement[0].GetProperty("provider").GetString());
    }
}
