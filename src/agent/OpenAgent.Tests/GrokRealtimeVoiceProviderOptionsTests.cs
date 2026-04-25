using OpenAgent.LlmVoice.GrokRealtime;
using OpenAgent.LlmVoice.GrokRealtime.Models;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAgent.Tests;

public class GrokRealtimeVoiceProviderOptionsTests
{
    [Fact]
    public async Task StartSessionAsync_WithUlawOptions_Throws_NotConfigured_NotCodecError()
    {
        var provider = new GrokRealtimeVoiceProvider(
            agentLogic: null!,
            logger: NullLogger<GrokRealtimeVoiceProvider>.Instance);

        var conversation = new Conversation
        {
            Id = "c1",
            Source = "test",
            Type = ConversationType.Voice,
            Provider = "grok-realtime-voice",
            Model = "grok-3-realtime"
        };

        var options = new VoiceSessionOptions("g711_ulaw", 8000);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StartSessionAsync(conversation, options));
        Assert.Contains("not been configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigFields_DoesNotIncludeCodecOrSampleRate()
    {
        var provider = new GrokRealtimeVoiceProvider(
            agentLogic: null!,
            logger: NullLogger<GrokRealtimeVoiceProvider>.Instance);

        Assert.DoesNotContain(provider.ConfigFields, f => f.Key == "codec");
        Assert.DoesNotContain(provider.ConfigFields, f => f.Key == "sampleRate");
    }

    [Fact]
    public void Session_RejectsCodecRateMismatch()
    {
        var config = new GrokConfig
        {
            ApiKey = "k",
            Voice = "rex"
        };
        var conversation = new Conversation
        {
            Id = "c1",
            Source = "t",
            Type = ConversationType.Voice,
            Provider = "grok-realtime-voice",
            Model = "grok-3-realtime"
        };
        var bad = new VoiceSessionOptions("g711_ulaw", 16000); // wrong rate for codec

        var ex = Assert.Throws<ArgumentException>(() =>
            new GrokVoiceSession(config, conversation, agentLogic: null!, bad,
                NullLogger<GrokVoiceSession>.Instance));
        Assert.Contains("8000", ex.Message);
        Assert.Equal("options", ex.ParamName);
    }
}
