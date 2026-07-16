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
/// Regression test for the tool-call/user-message ordering race in the Azure (Chat Completions)
/// provider: a user message persisted between an assistant tool_calls message and its tool result
/// used to orphan the round (only contiguous results were paired). BuildChatMessages now pairs the
/// result across the interleaved message and marks it consumed so the regular-message path does not
/// re-emit it out of order.
/// </summary>
public class AzureOpenAiTextProviderOrphanPairingTests
{
    [Fact]
    public async Task Tool_result_separated_from_its_call_by_a_user_message_is_paired_and_not_duplicated()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.GetOrCreate("conv-azure-orphan", "app", AzureOpenAiTextProvider.ProviderKey, "gpt-5.2-chat", "", "");
        var agentLogic = new SimpleAgentLogic(store);

        const string callId = "call_INTERLEAVED";

        store.AddMessage("conv-azure-orphan", new Message { Id = "a1", ConversationId = "conv-azure-orphan", Role = "assistant", Content = "", ToolCalls = $"[{{\"id\":\"{callId}\",\"type\":\"function\",\"function\":{{\"name\":\"shell_exec\",\"arguments\":\"{{}}\"}}}}]" });
        store.AddMessage("conv-azure-orphan", new Message { Id = "u1", ConversationId = "conv-azure-orphan", Role = "user", Content = "the radiology table has the scan" });
        store.AddMessage("conv-azure-orphan", new Message { Id = "t1", ConversationId = "conv-azure-orphan", Role = "tool", ToolCallId = callId, Content = "{\"tool\":\"shell_exec\",\"status\":\"ok\"}" });

        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"Got it.\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\ndata: [DONE]\n\n");

        var provider = new AzureOpenAiTextProvider(agentLogic, new AgentConfig(), NullLogger<AzureOpenAiTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { apiKey = "test-key", endpoint = "https://example-resource.openai.azure.com", apiVersion = "2025-04-01-preview" }));
        TextProviderHttpTestHelper.InstallStubHandler(provider, handler);

        var userMessage = new Message { Id = "u2", ConversationId = "conv-azure-orphan", Role = "user", Content = "so what did it say?", Modality = MessageModality.Text };

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None))
        {
        }

        Assert.Single(handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();

        // Assistant message carrying the tool_call must be present.
        Assert.Contains(messages, m =>
            m.TryGetProperty("role", out var r) && r.GetString() == "assistant"
            && m.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array
            && tcs.EnumerateArray().Any(tc => tc.TryGetProperty("id", out var id) && id.GetString() == callId));

        // Exactly one tool result for the call — paired in the round, not dropped, not duplicated.
        var toolResults = messages.Count(m =>
            m.TryGetProperty("role", out var r) && r.GetString() == "tool"
            && m.TryGetProperty("tool_call_id", out var id) && id.GetString() == callId);
        Assert.Equal(1, toolResults);

        // The interleaved user message must still be delivered.
        Assert.Contains(messages, m =>
            m.TryGetProperty("role", out var r) && r.GetString() == "user"
            && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            && c.GetString()!.Contains("the radiology table", StringComparison.Ordinal));
    }
}
