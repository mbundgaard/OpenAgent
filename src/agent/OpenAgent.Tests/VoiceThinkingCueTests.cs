using System.Text.Json;
using OpenAgent.Models.Voice;
using Xunit;

namespace OpenAgent.Tests;

public class VoiceThinkingCueTests
{
    [Fact]
    public void ThinkingStartedEvent_Serializes_As_thinking_started()
    {
        var json = JsonSerializer.Serialize(new VoiceThinkingStartedEvent());
        Assert.Contains("\"type\":\"thinking_started\"", json);
    }

    [Fact]
    public void ThinkingStoppedEvent_Serializes_As_thinking_stopped()
    {
        var json = JsonSerializer.Serialize(new VoiceThinkingStoppedEvent());
        Assert.Contains("\"type\":\"thinking_stopped\"", json);
    }
}
