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
/// Proves that AnthropicSubscriptionTextProvider.CompleteAsync(Conversation, ...) honors the
/// modelOverride parameter — the seam the background-agent heartbeat uses to run on a cheaper
/// model than the user's chat conversation (AgentConfig.BackgroundAgentModel), without mutating
/// the conversation's own persisted TextModel.
///
/// This test installs a stub HttpMessageHandler (via TextProviderHttpTestHelper — the provider
/// builds its own internal HttpClient in Configure() with no constructor injection point) and
/// inspects the actual outbound "model" field of the Anthropic Messages API request.
/// </summary>
public class AnthropicSubscriptionTextProviderModelOverrideTests
{
    private const string ConversationModel = "claude-sonnet-4-5";
    private const string OverrideModel = "claude-haiku-4-5";

    [Fact]
    public async Task ModelOverride_set_sends_the_override_model_not_the_conversations_TextModel()
    {
        var (provider, conversation, userMessage, handler) = Build();

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false, modelOverride: OverrideModel))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(OverrideModel, body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ModelOverride_null_sends_the_conversations_TextModel_unchanged()
    {
        var (provider, conversation, userMessage, handler) = Build();

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false, modelOverride: null))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(ConversationModel, body.RootElement.GetProperty("model").GetString());
    }

    private static (AnthropicSubscriptionTextProvider provider, Conversation conversation, Message userMessage, QueuedStubHttpMessageHandler handler) Build()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-anthropic-override", "app", AnthropicSubscriptionTextProvider.ProviderKey, ConversationModel, "", "");

        var agentLogic = new SimpleAgentLogic(store);

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicFinalTextSse("All quiet."));

        var provider = new AnthropicSubscriptionTextProvider(agentLogic, new AgentConfig(), NullLogger<AnthropicSubscriptionTextProvider>.Instance);
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
