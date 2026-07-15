using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.LlmText.OpenAIAzure;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Proves that AzureOpenAiTextProvider.CompleteAsync(Conversation, ...) honors the modelOverride
/// parameter. Azure encodes the model as the deployment-name path segment of the request URL
/// (there is no "model" field in the JSON body), so this test inspects the outbound URL rather
/// than the body — mirroring AnthropicSubscriptionTextProviderModelOverrideTests.
/// </summary>
public class AzureOpenAiTextProviderModelOverrideTests
{
    private const string ConversationModel = "gpt-5.2-chat";
    private const string OverrideModel = "gpt-5.2-chat-mini";

    [Fact]
    public async Task ModelOverride_set_sends_the_override_model_not_the_conversations_TextModel()
    {
        var (provider, conversation, userMessage, handler) = Build();

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false, modelOverride: OverrideModel))
        {
        }

        Assert.Single(handler.Requests);
        Assert.Contains($"/deployments/{OverrideModel}/", handler.Requests[0].Url);
    }

    [Fact]
    public async Task ModelOverride_null_sends_the_conversations_TextModel_unchanged()
    {
        var (provider, conversation, userMessage, handler) = Build();

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false, modelOverride: null))
        {
        }

        Assert.Single(handler.Requests);
        Assert.Contains($"/deployments/{ConversationModel}/", handler.Requests[0].Url);
    }

    private static (AzureOpenAiTextProvider provider, Conversation conversation, Message userMessage, QueuedStubHttpMessageHandler handler) Build()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-azure-override", "app", AzureOpenAiTextProvider.ProviderKey, ConversationModel, "", "");

        var agentLogic = new SimpleAgentLogic(store);

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildChatCompletionsSse(
            """{"choices":[{"delta":{"content":"All quiet."},"finish_reason":"stop"}],"usage":{"prompt_tokens":22,"completion_tokens":6}}"""));

        var provider = new AzureOpenAiTextProvider(agentLogic, new AgentConfig(), NullLogger<AzureOpenAiTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { apiKey = "test-key", endpoint = "https://example-resource.openai.azure.com", apiVersion = "2025-04-01-preview" }));
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

    private static string BuildChatCompletionsSse(params string[] dataLines)
    {
        var sb = new StringBuilder();
        foreach (var line in dataLines)
            sb.Append("data: ").Append(line).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }
}
