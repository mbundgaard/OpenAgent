using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.LlmText.AnthropicSubscription;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Proves that AnthropicSubscriptionTextProvider renders the ephemeral background-agent heartbeat
/// nudge (persistUserMessage: false) in its correct chronological position after a context-overflow
/// retry. Unlike OpenAiSubscriptionTextProvider, this provider APPENDS tool activity to its
/// in-memory message list across normal rounds (so the normal path was already correct) — the bug
/// only surfaces in the overflow-retry branch, which rebuilds the message list from persisted
/// history and used to re-append the never-persisted nudge at the END of that rebuilt list. When
/// overflow hits on round &gt;= 1, this turn's own tool_use/tool_result blocks are already
/// persisted, so the nudge landed AFTER them — the same "presented as the newest message" defect
/// described for the OpenAI Subscription provider, just reachable only via the overflow path.
///
/// No prior test exercised the real provider's outbound HTTP body. This test installs a stub
/// HttpMessageHandler (via TextProviderHttpTestHelper — the provider builds its own internal
/// HttpClient in Configure() with no constructor injection point) and inspects the actual
/// outbound JSON sent on the post-overflow retry.
/// </summary>
public class AnthropicSubscriptionTextProviderNudgeOrderingTests
{
    private const string NudgeText = "[Heartbeat] Reflect on the conversation and reply [] if nothing worth saying.";
    private const string PriorHistoryText = "Previous turn reply.";

    [Fact]
    public async Task PersistUserMessage_false_overflow_retry_body_has_nudge_exactly_once_before_tool_blocks()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-anthropic-false", "app", AnthropicSubscriptionTextProvider.ProviderKey, "claude-sonnet-4-5", "", "");

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
            // Simulate a successful compaction without actually trimming history — this test is
            // about message ORDER after the retry, not about compaction fidelity.
            CompactOverride = (_, _, _, _) => Task.FromResult(true)
        };

        var handler = new QueuedStubHttpMessageHandler();
        // Round 0: model calls the "note" tool.
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicToolUseSse());
        // Round 1, attempt 1: context overflow.
        handler.EnqueueJsonResponse(HttpStatusCode.BadRequest, """{"error":{"message":"prompt is too long: 250000 tokens > 200000 maximum"}}""");
        // Round 1, attempt 2 (post-compaction retry): final answer — this is the body under test.
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicFinalTextSse("All quiet."));

        var provider = new AnthropicSubscriptionTextProvider(agentLogic, new AgentConfig(), NullLogger<AnthropicSubscriptionTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { setupToken = "test-setup-token" }));
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
            var content = element.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (text == NudgeText) { nudgeCount++; nudgeIndex = i; }
                if (text == PriorHistoryText) historyIndex = i;
            }
            else if (content.ValueKind == JsonValueKind.Array && firstToolIndex == -1)
            {
                // tool_use / tool_result blocks are only ever carried as content arrays.
                firstToolIndex = i;
            }
            i++;
        }

        Assert.Equal(1, nudgeCount);
        Assert.True(historyIndex >= 0, "prior history message not found in the retry body");
        Assert.True(firstToolIndex >= 0, "no tool_use/tool_result block found in the retry body");
        Assert.True(historyIndex < nudgeIndex, $"history (index {historyIndex}) must precede the nudge (index {nudgeIndex})");
        Assert.True(nudgeIndex < firstToolIndex, $"nudge (index {nudgeIndex}) must precede this turn's tool activity (index {firstToolIndex}) — it must never be rendered as the newest message");

        Assert.DoesNotContain(store.GetMessages(conversation.Id), m => m.Content == NudgeText);
    }

    [Fact]
    public async Task PersistUserMessage_true_overflow_retry_body_has_user_message_exactly_once_from_history()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-anthropic-true", "app", AnthropicSubscriptionTextProvider.ProviderKey, "claude-sonnet-4-5", "", "");

        var agentLogic = new SimpleAgentLogic(store)
        {
            Tools = [new AgentToolDefinition { Name = "note", Description = "test tool", Parameters = new { type = "object", properties = new { } } }],
            ExecuteTool = (_, _) => Task.FromResult("{\"ok\":true}"),
            CompactOverride = (_, _, _, _) => Task.FromResult(true)
        };

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicToolUseSse());
        handler.EnqueueJsonResponse(HttpStatusCode.BadRequest, """{"error":{"message":"prompt is too long: 250000 tokens > 200000 maximum"}}""");
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicFinalTextSse("Done."));

        var provider = new AnthropicSubscriptionTextProvider(agentLogic, new AgentConfig(), NullLogger<AnthropicSubscriptionTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { setupToken = "test-setup-token" }));
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
            var content = element.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String && content.GetString() == userText)
                userCount++;
        }

        Assert.Equal(1, userCount);
    }

    private static string BuildAnthropicToolUseSse()
    {
        var sb = new StringBuilder();
        AppendEvent(sb, "message_start", """{"message":{"usage":{"input_tokens":10}}}""");
        AppendEvent(sb, "content_block_start", """{"index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"note"}}""");
        AppendEvent(sb, "content_block_delta", """{"index":0,"delta":{"type":"input_json_delta","partial_json":"{}"}}""");
        AppendEvent(sb, "message_delta", """{"delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}""");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
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
