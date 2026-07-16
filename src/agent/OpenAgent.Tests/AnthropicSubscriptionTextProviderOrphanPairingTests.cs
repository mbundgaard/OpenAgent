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
/// Regression test for the tool-call/user-message ordering race: when a user message is persisted
/// between an assistant <c>tool_use</c> and its <c>tool_result</c> (which happens when the user
/// sends a message while a tool is still running), the round used to be treated as orphaned and
/// dropped from the LLM view entirely. BuildMessages now pairs the tool_use with its later
/// tool_result across the interleaved message, so the round survives and the interleaved message
/// is still delivered.
/// </summary>
public class AnthropicSubscriptionTextProviderOrphanPairingTests
{
    [Fact]
    public async Task Tool_result_separated_from_its_call_by_a_user_message_is_still_paired()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-orphan", "app", AnthropicSubscriptionTextProvider.ProviderKey, "claude-sonnet-5", "", "");
        var agentLogic = new SimpleAgentLogic(store);

        const string toolId = "toolu_INTERLEAVED";

        // assistant tool_use  →  user message (arrived mid-tool)  →  tool result
        store.AddMessage("conv-orphan", new Message { Id = "a1", ConversationId = "conv-orphan", Role = "assistant", Content = "", ToolCalls = $"[{{\"id\":\"{toolId}\",\"type\":\"function\",\"function\":{{\"name\":\"shell_exec\",\"arguments\":\"{{}}\"}}}}]" });
        store.AddMessage("conv-orphan", new Message { Id = "u1", ConversationId = "conv-orphan", Role = "user", Content = "the radiology table has the scan" });
        store.AddMessage("conv-orphan", new Message { Id = "t1", ConversationId = "conv-orphan", Role = "tool", ToolCallId = toolId, Content = "{\"tool\":\"shell_exec\",\"status\":\"ok\"}" });

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicFinalTextSse("Got it."));

        var provider = new AnthropicSubscriptionTextProvider(agentLogic, new AgentConfig(), NullLogger<AnthropicSubscriptionTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { setupToken = "test-setup-token" }));
        TextProviderHttpTestHelper.InstallStubHandler(provider, handler);

        var userMessage = new Message { Id = "u2", ConversationId = "conv-orphan", Role = "user", Content = "so what did it say?", Modality = MessageModality.Text };

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var messages = body.RootElement.GetProperty("messages");

        Assert.True(HasToolUse(messages, toolId), "outbound request must contain the assistant tool_use — the round must not be dropped");
        Assert.True(HasToolResult(messages, toolId), "outbound request must contain the paired tool_result");
        Assert.True(HasUserText(messages, "the radiology table"), "the interleaved user message must still be delivered");
    }

    private static bool HasToolUse(JsonElement messages, string toolId) =>
        HasBlock(messages, "tool_use", "id", toolId);

    private static bool HasToolResult(JsonElement messages, string toolId) =>
        HasBlock(messages, "tool_result", "tool_use_id", toolId);

    private static bool HasBlock(JsonElement messages, string blockType, string idProp, string idValue)
    {
        foreach (var msg in messages.EnumerateArray())
        {
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (block.TryGetProperty("type", out var t) && t.GetString() == blockType
                    && block.TryGetProperty(idProp, out var id) && id.GetString() == idValue)
                    return true;
            }
        }
        return false;
    }

    private static bool HasUserText(JsonElement messages, string substring)
    {
        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.TryGetProperty("role", out var r) && r.GetString() == "user"
                && msg.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString()!.Contains(substring, StringComparison.Ordinal))
                return true;
        }
        return false;
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
