using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.ConversationStore.Sqlite;
using OpenAgent.LlmText.AnthropicSubscription;
using OpenAgent.Models.Common;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;

namespace OpenAgent.Tests;

/// <summary>
/// Regression test for the premature-compaction bug: the provider computed the model's context
/// window at turn start but persisted a freshly re-read conversation at turn end that never carried
/// it, so <see cref="Conversation.ContextWindowTokens"/> stayed null forever and the compaction
/// threshold always fell back to <see cref="CompactionConfig.MaxContextTokens"/> (400k) regardless
/// of the model's real window. On sonnet-5 (1M window) this fired compaction at 280k instead of
/// 700k.
///
/// Uses the real <see cref="SqliteConversationStore"/> — not the in-memory fake — because the bug
/// depends on <c>Get()</c> returning a fresh DB row that drops in-memory mutations. The in-memory
/// fake returns the same object reference and would mask it.
/// </summary>
public sealed class AnthropicSubscriptionTextProviderContextWindowTests : IDisposable
{
    private readonly string _dbDir;
    private readonly SqliteConversationStore _store;

    public AnthropicSubscriptionTextProviderContextWindowTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), $"openagent-cwt-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_dbDir);
        var env = new AgentEnvironment { DataPath = _dbDir };
        _store = new SqliteConversationStore(env, NullLogger<SqliteConversationStore>.Instance, new CompactionConfig());
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dbDir, true); } catch { }
    }

    [Fact]
    public async Task Turn_persists_the_models_context_window_for_the_compaction_threshold()
    {
        var conversation = _store.GetOrCreate("conv-cwt", "app", AnthropicSubscriptionTextProvider.ProviderKey, "claude-sonnet-5", "", "");
        Assert.Null(conversation.ContextWindowTokens);

        var agentLogic = new SimpleAgentLogic(_store);
        var handler = new QueuedStubHttpMessageHandler();
        handler.EnqueueSseResponse(HttpStatusCode.OK, BuildAnthropicFinalTextSse("Noted."));

        var provider = new AnthropicSubscriptionTextProvider(agentLogic, new AgentConfig(), NullLogger<AnthropicSubscriptionTextProvider>.Instance);
        provider.Configure(JsonSerializer.SerializeToElement(new { setupToken = "test-setup-token" }));
        TextProviderHttpTestHelper.InstallStubHandler(provider, handler);

        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = "hello",
            Modality = MessageModality.Text
        };

        await foreach (var _ in provider.CompleteAsync(conversation, userMessage, CancellationToken.None))
        {
        }

        var persisted = _store.Get("conv-cwt");
        Assert.NotNull(persisted);
        Assert.Equal(1_000_000, persisted!.ContextWindowTokens);
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
