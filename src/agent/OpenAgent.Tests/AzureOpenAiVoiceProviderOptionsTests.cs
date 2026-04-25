using OpenAgent.LlmVoice.OpenAIAzure;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAgent.Tests;

public class AzureOpenAiVoiceProviderOptionsTests
{
    [Fact]
    public async Task StartSessionAsync_WithUlawOptions_Throws_NotConfigured_NotCodecError()
    {
        var provider = new AzureOpenAiRealtimeVoiceProvider(
            agentLogic: null!,
            logger: NullLogger<AzureOpenAiRealtimeVoiceProvider>.Instance);

        var conversation = new Conversation
        {
            Id = "c1",
            Source = "test",
            Type = ConversationType.Voice,
            Provider = "azure-openai-voice",
            Model = "gpt-realtime"
        };

        var options = new VoiceSessionOptions("g711_ulaw", 8000);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StartSessionAsync(conversation, options));
        Assert.Contains("not been configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigFields_DoesNotIncludeCodecOrSampleRate()
    {
        var provider = new AzureOpenAiRealtimeVoiceProvider(
            agentLogic: null!,
            logger: NullLogger<AzureOpenAiRealtimeVoiceProvider>.Instance);

        Assert.DoesNotContain(provider.ConfigFields, f => f.Key == "codec");
        Assert.DoesNotContain(provider.ConfigFields, f => f.Key == "sampleRate");
    }
}
