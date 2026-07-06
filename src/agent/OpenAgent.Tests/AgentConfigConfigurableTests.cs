using System.Text.Json;
using OpenAgent.Models.Configs;

namespace OpenAgent.Tests;

public class AgentConfigConfigurableTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Configure_BackgroundAgentEnabled_BoolTrue_IsApplied()
    {
        var config = new AgentConfig();
        var configurable = new AgentConfigConfigurable(config);

        configurable.Configure(Json("""{ "backgroundAgentEnabled": true }"""));

        Assert.True(config.BackgroundAgentEnabled);
    }

    [Fact]
    public void Configure_BackgroundAgentEnabled_StringTrue_IsApplied()
    {
        // The admin UI sends field values as strings.
        var config = new AgentConfig();
        var configurable = new AgentConfigConfigurable(config);

        configurable.Configure(Json("""{ "backgroundAgentEnabled": "true" }"""));

        Assert.True(config.BackgroundAgentEnabled);
    }

    [Fact]
    public void Configure_BackgroundAgentEnabled_False_IsApplied()
    {
        var config = new AgentConfig { BackgroundAgentEnabled = true };
        var configurable = new AgentConfigConfigurable(config);

        configurable.Configure(Json("""{ "backgroundAgentEnabled": false }"""));

        Assert.False(config.BackgroundAgentEnabled);
    }

    [Fact]
    public void Configure_BackgroundAgentEnabled_Absent_LeavesValueUnchanged()
    {
        var config = new AgentConfig { BackgroundAgentEnabled = true };
        var configurable = new AgentConfigConfigurable(config);

        configurable.Configure(Json("""{ "textProvider": "anthropic-subscription" }"""));

        Assert.True(config.BackgroundAgentEnabled);
    }

    [Fact]
    public void ConfigFields_IncludesBackgroundAgentEnabled()
    {
        var configurable = new AgentConfigConfigurable(new AgentConfig());

        Assert.Contains(configurable.ConfigFields, f => f.Key == "backgroundAgentEnabled");
    }
}
