using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.LlmText.OpenAIAzure;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Proves that AzureOpenAiTextProvider renders the ephemeral background-agent heartbeat nudge
/// (persistUserMessage: false) in its correct chronological position after a context-overflow
/// retry. Structurally identical to the Anthropic provider's bug and fix: normal tool-call rounds
/// append to the in-memory request.Messages list (already correct), but the overflow-retry branch
/// used to rebuild request.Messages from persisted history and re-append the never-persisted nudge
/// at the END — landing it after this turn's own already-persisted assistant/tool messages when
/// overflow hit on round &gt;= 1.
///
/// No prior test exercised the real provider's outbound HTTP body. This test installs a stub
/// HttpMessageHandler (via TextProviderHttpTestHelper — the provider builds its own internal
/// HttpClient in Configure() with no constructor injection point) and inspects the actual
/// outbound JSON sent on the post-overflow retry.
/// </summary>
public class AzureOpenAiTextProviderNudgeOrderingTests
{
    private const string NudgeText = "[Heartbeat] Reflect on the conversation and reply [] if nothing worth saying.";
    private const string PriorHistoryText = "Previous turn reply.";

    [Fact]
    public async Task PersistUserMessage_false_overflow_retry_body_has_nudge_exactly_once_before_tool_messages()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-azure-false", "app", AzureOpenAiTextProvider.ProviderKey, "gpt-5.2-chat", "", "");

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
            ExecuteTool = (_, _) => Task.FromResult("{\"ok\":true}"),
            CompactOverride = (_, _, _, _) => Task.FromResult(true)
        };

        var handler = new QueuedStubHttpMessageHandler();
        // Round 0: model calls the "note" tool.
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildChatCompletionsSse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"note","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":4}}"""));
        // Round 1, attempt 1: context overflow.
        handler.EnqueueJsonResponse(HttpStatusCode.BadRequest, """{"error":{"code":"context_length_exceeded","message":"maximum context length exceeded"}}""");
        // Round 1, attempt 2 (post-compaction retry): final answer — this is the body under test.
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
            Content = NudgeText,
            Modality = MessageModality.Text
        };

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None, persistUserMessage: false))
        {
        }

        Assert.Equal(3, handler.Requests.Count);

        using var retryBody = JsonDocument.Parse(handler.Requests[2].Body);
        var messages = retryBody.RootElement.GetProperty("messages");

        var historyIndex = -1;
        var nudgeIndex = -1;
        var nudgeCount = 0;
        var firstToolIndex = -1;
        var i = 0;
        foreach (var element in messages.EnumerateArray())
        {
            var role = element.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null;

            if (element.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                var text = contentProp.GetString();
                if (text == NudgeText) { nudgeCount++; nudgeIndex = i; }
                if (text == PriorHistoryText) historyIndex = i;
            }

            var isToolActivity = role == "tool" || (role == "assistant" && element.TryGetProperty("tool_calls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array);
            if (isToolActivity && firstToolIndex == -1)
                firstToolIndex = i;

            i++;
        }

        Assert.Equal(1, nudgeCount);
        Assert.True(historyIndex >= 0, "prior history message not found in the retry body");
        Assert.True(firstToolIndex >= 0, "no tool-call/tool-result message found in the retry body");
        Assert.True(historyIndex < nudgeIndex, $"history (index {historyIndex}) must precede the nudge (index {nudgeIndex})");
        Assert.True(nudgeIndex < firstToolIndex, $"nudge (index {nudgeIndex}) must precede this turn's tool activity (index {firstToolIndex}) — it must never be rendered as the newest message");

        Assert.DoesNotContain(store.GetMessages(conversation.Id), m => m.Content == NudgeText);
    }

    [Fact]
    public async Task PersistUserMessage_true_overflow_retry_body_has_user_message_exactly_once_from_history()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-azure-true", "app", AzureOpenAiTextProvider.ProviderKey, "gpt-5.2-chat", "", "");

        var agentLogic = new SimpleAgentLogic(store)
        {
            Tools = [new AgentToolDefinition { Name = "note", Description = "test tool", Parameters = new { type = "object", properties = new { } } }],
            ExecuteTool = (_, _) => Task.FromResult("{\"ok\":true}"),
            CompactOverride = (_, _, _, _) => Task.FromResult(true)
        };

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildChatCompletionsSse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"note","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":4}}"""));
        handler.EnqueueJsonResponse(HttpStatusCode.BadRequest, """{"error":{"code":"context_length_exceeded","message":"maximum context length exceeded"}}""");
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildChatCompletionsSse(
            """{"choices":[{"delta":{"content":"Done."},"finish_reason":"stop"}],"usage":{"prompt_tokens":22,"completion_tokens":6}}"""));

        var provider = new AzureOpenAiTextProvider(agentLogic, new AgentConfig(), NullLogger<AzureOpenAiTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { apiKey = "test-key", endpoint = "https://example-resource.openai.azure.com", apiVersion = "2025-04-01-preview" }));
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

        Assert.Single(store.GetMessages(conversation.Id), m => m.Content == userText);

        using var retryBody = JsonDocument.Parse(handler.Requests[2].Body);
        var messages = retryBody.RootElement.GetProperty("messages");

        var userCount = 0;
        foreach (var element in messages.EnumerateArray())
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

    private static string BuildChatCompletionsSse(params string[] dataLines)
    {
        var sb = new StringBuilder();
        foreach (var line in dataLines)
            sb.Append("data: ").Append(line).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }
}
