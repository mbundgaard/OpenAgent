using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.LlmText.OpenAISubscription;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Proves that OpenAiSubscriptionTextProvider renders the ephemeral background-agent heartbeat
/// nudge (persistUserMessage: false) into the outbound Responses API request body in its correct
/// chronological position — before this turn's own tool activity — on every round, not just the
/// first. This provider was the worst case identified in review: BuildInputForTurn used to be
/// re-derived from persisted history on every tool-call round, which placed the never-persisted
/// nudge AFTER this turn's own function_call/function_call_output items once any tool round had
/// completed (they, unlike the nudge, are already in the store by then). That ordering made the
/// model treat its own tool output as stale and re-read the "newest" message (the nudge) forever,
/// which is the documented "Tool call loop exceeded N rounds" failure mode.
///
/// No prior test exercised the real provider's outbound HTTP body at all — existing fakes
/// (PersistingTextProvider, MultiRoundTextProvider) only prove the runner passes
/// persistUserMessage: false through, not that the real wire-format renderer places it correctly.
/// This test installs a stub HttpMessageHandler (via TextProviderHttpTestHelper, the smallest
/// available seam — the provider builds its own internal HttpClient in Configure() with no
/// constructor injection point) and inspects the actual outbound JSON.
/// </summary>
public class OpenAiSubscriptionTextProviderNudgeOrderingTests
{
    private const string NudgeText = "[Heartbeat] Reflect on the conversation and reply [] if nothing worth saying.";
    private const string PriorHistoryText = "Previous turn reply.";

    [Fact]
    public async Task PersistUserMessage_false_round2_body_has_nudge_exactly_once_before_tool_items()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-openai-false", "app", OpenAiSubscriptionTextProvider.ProviderKey, "gpt-5.3-codex", "", "");

        // Prior turn's assistant reply — already persisted before this turn starts, so it must
        // appear BEFORE the nudge in every rebuilt request.
        store.AddMessage(conversation.Id, new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = PriorHistoryText,
            Modality = MessageModality.Text
        });

        var agentLogic = new SimpleAgentLogic(store)
        {
            Tools = [new AgentToolDefinition { Name = "note", Description = "test tool", Parameters = new { type = "object", properties = new { } } }],
            ExecuteTool = (_, _) => Task.FromResult("{\"ok\":true}")
        };

        var handler = new QueuedStubHttpMessageHandler();
        // Round 1: model calls the "note" tool.
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildResponsesSse(
            """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_1","id":"item_1","name":"note","arguments":"{}"}}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":11,"output_tokens":4}}}"""));
        // Round 2: model gives a final answer — this is the request body under test.
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
            Content = NudgeText,
            Modality = MessageModality.Text
        };

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false))
        {
            // Drain — we only care about what hit the wire.
        }

        Assert.Equal(2, handler.Requests.Count);

        using var round2Body = JsonDocument.Parse(handler.Requests[1].Body);
        var input = round2Body.RootElement.GetProperty("input");

        var historyIndex = -1;
        var nudgeIndex = -1;
        var nudgeCount = 0;
        var firstToolIndex = -1;
        var i = 0;
        foreach (var element in input.EnumerateArray())
        {
            if (element.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var text = contentProp.GetString();
                if (text == NudgeText) { nudgeCount++; nudgeIndex = i; }
                if (text == PriorHistoryText) historyIndex = i;
            }
            else if (element.TryGetProperty("type", out var typeProp)
                     && typeProp.GetString() is "function_call" or "function_call_output"
                     && firstToolIndex == -1)
            {
                firstToolIndex = i;
            }
            i++;
        }

        Assert.Equal(1, nudgeCount);
        Assert.True(historyIndex >= 0, "prior history message not found in round 2 body");
        Assert.True(firstToolIndex >= 0, "no function_call/function_call_output item found in round 2 body");
        Assert.True(historyIndex < nudgeIndex, $"history (index {historyIndex}) must precede the nudge (index {nudgeIndex})");
        Assert.True(nudgeIndex < firstToolIndex, $"nudge (index {nudgeIndex}) must precede this turn's tool activity (index {firstToolIndex}) — it must never be rendered as the newest message");

        // The nudge itself must never have been written to the store.
        Assert.DoesNotContain(store.GetMessages(conversation.Id), m => m.Content == NudgeText);
    }

    [Fact]
    public async Task PersistUserMessage_true_round2_body_has_user_message_exactly_once_from_history()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-openai-true", "app", OpenAiSubscriptionTextProvider.ProviderKey, "gpt-5.3-codex", "", "");

        var agentLogic = new SimpleAgentLogic(store)
        {
            Tools = [new AgentToolDefinition { Name = "note", Description = "test tool", Parameters = new { type = "object", properties = new { } } }],
            ExecuteTool = (_, _) => Task.FromResult("{\"ok\":true}")
        };

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildResponsesSse(
            """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_1","id":"item_1","name":"note","arguments":"{}"}}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":11,"output_tokens":4}}}"""));
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildResponsesSse(
            """{"type":"response.output_text.delta","delta":"Done."}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":22,"output_tokens":6}}}"""));

        var provider = new OpenAiSubscriptionTextProvider(agentLogic, new NoopConfigStore(), new AgentConfig(), NullLogger<OpenAiSubscriptionTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { setupToken = FakeJwt() }));
        TextProviderHttpTestHelper.InstallStubHandler(provider, handler);

        const string userText = "Please check the last thread.";
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = userText,
            Modality = MessageModality.Text
        };

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: true))
        {
        }

        // Normal chat behavior must be unchanged: the message is persisted exactly once.
        Assert.Single(store.GetMessages(conversation.Id), m => m.Content == userText);

        using var round2Body = JsonDocument.Parse(handler.Requests[1].Body);
        var input = round2Body.RootElement.GetProperty("input");

        var userCount = 0;
        foreach (var element in input.EnumerateArray())
        {
            if (element.TryGetProperty("content", out var contentProp)
                && contentProp.ValueKind == JsonValueKind.String
                && contentProp.GetString() == userText)
            {
                userCount++;
            }
        }

        Assert.Equal(1, userCount);
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
