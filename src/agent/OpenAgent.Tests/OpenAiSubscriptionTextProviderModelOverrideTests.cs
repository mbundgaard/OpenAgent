using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.LlmText.OpenAISubscription;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Proves that OpenAiSubscriptionTextProvider.CompleteAsync(Conversation, ...) honors the
/// modelOverride parameter — the seam the background-agent heartbeat uses to run on a cheaper
/// model than the user's chat conversation.
/// </summary>
public class OpenAiSubscriptionTextProviderModelOverrideTests
{
    private const string ConversationModel = "gpt-5.3-codex";
    private const string OverrideModel = "gpt-5.4-mini";

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

    private static (OpenAiSubscriptionTextProvider provider, Conversation conversation, Message userMessage, QueuedStubHttpMessageHandler handler) Build()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-openai-override", "app", OpenAiSubscriptionTextProvider.ProviderKey, ConversationModel, "", "");

        var agentLogic = new SimpleAgentLogic(store);

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildResponsesSse(
            """{"type":"response.output_text.delta","delta":"All quiet."}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":22,"output_tokens":6}}}"""));

        var provider = new OpenAiSubscriptionTextProvider(agentLogic, new NoopConfigStore(), new AgentConfig(), NullLogger<OpenAiSubscriptionTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { setupToken = FakeJwt() }));
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

    private static string BuildResponsesSse(params string[] dataLines)
    {
        var sb = new StringBuilder();
        foreach (var line in dataLines)
            sb.Append("data: ").Append(line).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a syntactically valid (unsigned) JWT carrying the chatgpt_account_id claim the
    /// provider's ExtractAccountId reads from the setup token — the provider treats the setup
    /// token as an opaque JWT and never verifies the signature itself.
    /// </summary>
    private static string FakeJwt()
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"none"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct_test"}}"""));
        var signature = Base64Url(Encoding.UTF8.GetBytes("sig"));
        return $"{header}.{payload}.{signature}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
