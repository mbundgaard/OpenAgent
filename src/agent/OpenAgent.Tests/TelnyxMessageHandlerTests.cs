using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

public class TelnyxMessageHandlerTests
{
    [Fact]
    public async Task Voice_creates_conversation_and_returns_greeting_with_gather()
    {
        var (handler, store, _) = BuildHandler(
            options: new TelnyxOptions { BaseUrl = "https://example.com", WebhookId = "abc" });

        var xml = await handler.HandleVoiceAsync(
            callSid: "call-123",
            from: "+4512345678",
            to: "+4598765432",
            ct: default);

        Assert.Contains("<Gather", xml);
        Assert.Contains("action=\"https://example.com/api/webhook/telnyx/abc/speech\"", xml);
        Assert.NotNull(store.FindChannelConversation("telnyx", "conn-1", "+4512345678"));
    }

    [Fact]
    public async Task Speech_appends_user_message_and_returns_agent_reply()
    {
        var fakeProvider = new FakeTelnyxTextProvider(reply: "The answer is 42.");
        var (handler, store, _) = BuildHandler(
            options: new TelnyxOptions { BaseUrl = "https://example.com", WebhookId = "abc" },
            provider: fakeProvider);

        // Seed the conversation by calling voice first.
        await handler.HandleVoiceAsync("call-123", "+4512345678", "+4598765432", default);

        var xml = await handler.HandleSpeechAsync(
            callSid: "call-123",
            from: "+4512345678",
            speechResult: "What is the answer?",
            ct: default);

        Assert.Contains("<Say>The answer is 42.</Say>", xml);
        Assert.Contains("<Gather", xml);

        Assert.NotNull(fakeProvider.LastConversation);
        Assert.Equal("What is the answer?", fakeProvider.LastUserMessage!.Content);
    }

    [Fact]
    public async Task Voice_rejects_caller_not_on_nonempty_allowlist()
    {
        var options = new TelnyxOptions
        {
            BaseUrl = "https://example.com",
            WebhookId = "abc",
            AllowedNumbers = ["+4599999999"],
        };
        var (handler, store, _) = BuildHandler(options);

        var xml = await handler.HandleVoiceAsync("call-x", "+4512345678", "+4598765432", default);

        Assert.Contains("<Hangup />", xml);
        Assert.DoesNotContain("<Gather", xml);
        Assert.Null(store.FindChannelConversation("telnyx", "conn-1", "+4512345678"));
    }

    [Fact]
    public async Task Voice_allows_caller_on_allowlist()
    {
        var options = new TelnyxOptions
        {
            BaseUrl = "https://example.com",
            WebhookId = "abc",
            AllowedNumbers = ["+4512345678"],
        };
        var (handler, store, _) = BuildHandler(options);

        var xml = await handler.HandleVoiceAsync("call-x", "+4512345678", "+4598765432", default);

        Assert.Contains("<Gather", xml);
        Assert.NotNull(store.FindChannelConversation("telnyx", "conn-1", "+4512345678"));
    }

    [Fact]
    public async Task Empty_speech_result_produces_reprompt_not_forwarded_to_provider()
    {
        var fakeProvider = new FakeTelnyxTextProvider(reply: "ignored");
        var (handler, _, _) = BuildHandler(
            new TelnyxOptions { BaseUrl = "https://example.com", WebhookId = "abc" },
            provider: fakeProvider);

        await handler.HandleVoiceAsync("call-x", "+4512345678", "+4598765432", default);

        var xml = await handler.HandleSpeechAsync("call-x", "+4512345678", speechResult: "", ct: default);

        Assert.Contains("<Gather", xml);
        Assert.Null(fakeProvider.LastUserMessage); // provider was never invoked
    }

    // Helpers
    private static (TelnyxMessageHandler handler, InMemoryConversationStore store, AgentConfig config) BuildHandler(
        TelnyxOptions options,
        ILlmTextProvider? provider = null)
    {
        var store = new InMemoryConversationStore();
        var agentConfig = new AgentConfig { TextProvider = "fake", TextModel = "fake-1" };
        Func<string, ILlmTextProvider> resolver = _ => provider ?? new FakeTelnyxTextProvider("hello");
        var handler = new TelnyxMessageHandler(
            options,
            "conn-1",
            store,
            resolver,
            agentConfig,
            NullLogger<TelnyxMessageHandler>.Instance);
        return (handler, store, agentConfig);
    }
}
