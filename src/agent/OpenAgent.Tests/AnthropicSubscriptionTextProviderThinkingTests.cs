using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.LlmText.AnthropicSubscription;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Proves that AnthropicSubscriptionTextProvider sends the correct thinking/effort wire fields
/// for both CompleteAsync overloads, per the ResolveThinking gate: thinking + output_config.effort
/// are sent only for adaptive-thinking-capable models, and only when the configured/override
/// spec is not "off". Haiku 4.5 (not adaptive-thinking-capable) must never receive either field,
/// even when configured to think — sending output_config.effort to Haiku 400s.
///
/// Installs a stub HttpMessageHandler (via TextProviderHttpTestHelper) and inspects the actual
/// outbound JSON body sent to the Anthropic Messages API, matching the pattern used by
/// AnthropicSubscriptionTextProviderModelOverrideTests and
/// AnthropicSubscriptionTextProviderNudgeOrderingTests.
/// </summary>
public class AnthropicSubscriptionTextProviderThinkingTests
{
    private const string SonnetModel = "claude-sonnet-5";
    private const string HaikuModel = "claude-haiku-4-5";

    [Fact]
    public async Task ConversationOverload_AdaptiveModel_TextThinkingHigh_sends_adaptive_thinking_and_high_effort()
    {
        var agentConfig = new AgentConfig { TextThinking = "high" };
        var (provider, conversation, userMessage, handler) = Build(SonnetModel, agentConfig);

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var root = body.RootElement;

        Assert.True(root.TryGetProperty("thinking", out var thinking));
        Assert.Equal("adaptive", thinking.GetProperty("type").GetString());

        Assert.True(root.TryGetProperty("output_config", out var outputConfig));
        Assert.Equal("high", outputConfig.GetProperty("effort").GetString());
    }

    [Fact]
    public async Task ConversationOverload_ThinkingOverrideOff_AdaptiveModel_sends_no_thinking_and_no_output_config()
    {
        // AgentConfig.TextThinking defaults to "high" - the override must win regardless.
        var agentConfig = new AgentConfig();
        var (provider, conversation, userMessage, handler) = Build(SonnetModel, agentConfig);

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false, thinkingOverride: "off"))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var root = body.RootElement;

        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task ConversationOverload_HaikuModel_ConfiguredHigh_sends_no_thinking_and_no_output_config()
    {
        // Haiku 4.5 is not in AdaptiveThinkingModels - it must never receive thinking/effort,
        // even though TextThinking is configured to "high". Sending output_config.effort to
        // Haiku 400s on the real API.
        var agentConfig = new AgentConfig { TextThinking = "high" };
        var (provider, conversation, userMessage, handler) = Build(HaikuModel, agentConfig);

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var root = body.RootElement;

        Assert.Equal(HaikuModel, root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task MessagesOverload_ThinkingOff_sends_no_thinking_and_no_output_config()
    {
        var provider = BuildRawProvider();
        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicFinalTextSse("ok"));
        TextProviderHttpTestHelper.InstallStubHandler(provider, handler);

        var messages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = "system prompt" },
            new() { Id = "usr", ConversationId = "", Role = "user", Content = "hello" }
        };
        var options = new CompletionOptions { Thinking = "off" };

        await foreach (var _ in provider.CompleteAsync(messages, SonnetModel, options, CancellationToken.None))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var root = body.RootElement;

        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task MessagesOverload_ThinkingMedium_sends_adaptive_thinking_and_medium_effort()
    {
        var provider = BuildRawProvider();
        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicFinalTextSse("ok"));
        TextProviderHttpTestHelper.InstallStubHandler(provider, handler);

        var messages = new List<Message>
        {
            new() { Id = "sys", ConversationId = "", Role = "system", Content = "system prompt" },
            new() { Id = "usr", ConversationId = "", Role = "user", Content = "hello" }
        };
        var options = new CompletionOptions { Thinking = "medium" };

        await foreach (var _ in provider.CompleteAsync(messages, SonnetModel, options, CancellationToken.None))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var root = body.RootElement;

        Assert.True(root.TryGetProperty("thinking", out var thinking));
        Assert.Equal("adaptive", thinking.GetProperty("type").GetString());

        Assert.True(root.TryGetProperty("output_config", out var outputConfig));
        Assert.Equal("medium", outputConfig.GetProperty("effort").GetString());
    }

    private static (AnthropicSubscriptionTextProvider provider, Conversation conversation, Message userMessage, QueuedStubHttpMessageHandler handler) Build(
        string conversationModel, AgentConfig agentConfig)
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-anthropic-thinking-" + Guid.NewGuid(), "app", AnthropicSubscriptionTextProvider.ProviderKey, conversationModel, "", "");

        var agentLogic = new SimpleAgentLogic(store);

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicFinalTextSse("All quiet."));

        var provider = new AnthropicSubscriptionTextProvider(agentLogic, agentConfig, NullLogger<AnthropicSubscriptionTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { setupToken = "test-setup-token" }));
        TextProviderHttpTestHelper.InstallStubHandler(provider, handler);

        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = "[Heartbeat] anything worth saying?",
            Modality = MessageModality.Text
        };

        return (provider, conversation, userMessage, handler);
    }

    private static AnthropicSubscriptionTextProvider BuildRawProvider()
    {
        var store = new InMemoryConversationStore();
        var agentLogic = new SimpleAgentLogic(store);
        var provider = new AnthropicSubscriptionTextProvider(agentLogic, new AgentConfig(), NullLogger<AnthropicSubscriptionTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { setupToken = "test-setup-token" }));
        return provider;
    }

    private static string BuildAnthropicFinalTextSse(string text)
    {
        var sb = new StringBuilder();
        AppendEvent(sb, "message_start", """{"message":{"usage":{"input_tokens":30}}}""");
        AppendEvent(sb, "content_block_start", """{"index":0,"content_block":{"type":"text"}}""");
        AppendEvent(sb, "content_block_delta", JsonSerializer.Serialize(new { index = 0, delta = new { type = "text_delta", text } }));
        AppendEvent(sb, "message_delta", """{"delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":6}}""");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private static void AppendEvent(StringBuilder sb, string eventType, string dataJson)
    {
        sb.Append("event: ").Append(eventType).Append('\n');
        sb.Append("data: ").Append(dataJson).Append("\n\n");
    }
}
