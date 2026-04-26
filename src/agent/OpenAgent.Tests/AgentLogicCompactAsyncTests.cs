using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.Tests;

/// <summary>
/// Minimum-viable smoke test for the overflow retry path. Real HTTP-level verification
/// requires mocking HttpClient or hitting a real model at its context limit; this test
/// just verifies that IAgentLogic.CompactAsync flows to IConversationStore.CompactNowAsync
/// with the same reason and custom instructions.
/// </summary>
public class AgentLogicCompactAsyncTests
{
    [Fact]
    public async Task CompactAsync_with_Overflow_flows_to_store_with_same_reason()
    {
        var store = new RecordingStore();
        var agentLogic = new TestAgentLogic(store);

        await agentLogic.CompactAsync("conv1", CompactionReason.Overflow, "focus on last turn");

        Assert.Equal("conv1", store.LastConversationId);
        Assert.Equal(CompactionReason.Overflow, store.LastReason);
        Assert.Equal("focus on last turn", store.LastCustomInstructions);
    }

    [Fact]
    public async Task CompactAsync_with_Manual_flows_to_store_with_same_reason()
    {
        var store = new RecordingStore();
        var agentLogic = new TestAgentLogic(store);

        await agentLogic.CompactAsync("conv2", CompactionReason.Manual, null);

        Assert.Equal(CompactionReason.Manual, store.LastReason);
        Assert.Null(store.LastCustomInstructions);
    }

    private sealed class RecordingStore : IConversationStore
    {
        public string? LastConversationId { get; private set; }
        public CompactionReason? LastReason { get; private set; }
        public string? LastCustomInstructions { get; private set; }

        public Task<bool> CompactNowAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default)
        {
            LastConversationId = conversationId;
            LastReason = reason;
            LastCustomInstructions = customInstructions;
            return Task.FromResult(true);
        }

        // IConfigurable — no-op
        public string Key => "recording";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }

        // Everything else throws — these tests don't exercise them.
        public Conversation GetOrCreate(string conversationId, string source, ConversationType type, string provider, string model) => throw new NotImplementedException();
        public Conversation? FindChannelConversation(string channelType, string connectionId, string channelChatId) => throw new NotImplementedException();
        public Conversation FindOrCreateChannelConversation(string channelType, string connectionId, string channelChatId, string source, ConversationType type, string provider, string model) => throw new NotImplementedException();
        public IReadOnlyList<Conversation> GetAll() => throw new NotImplementedException();
        public Conversation? Get(string conversationId) => throw new NotImplementedException();
        public void Update(Conversation conversation) => throw new NotImplementedException();
        public void UpdateType(string conversationId, ConversationType type) => throw new NotImplementedException();
        public void UpdateDisplayName(string conversationId, string? displayName) => throw new NotImplementedException();
        public bool Delete(string conversationId) => throw new NotImplementedException();
        public void AddMessage(string conversationId, Message message) => throw new NotImplementedException();
        public void UpdateChannelMessageId(string messageId, string channelMessageId) => throw new NotImplementedException();
        public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false) => throw new NotImplementedException();
        public IReadOnlyList<Message> GetMessagesByIds(IReadOnlyList<string> messageIds, bool includeToolResultBlobs = false) => throw new NotImplementedException();
    }

    // Minimal IAgentLogic impl that only forwards CompactAsync to the store. Avoids
    // pulling in the real AgentLogic's SystemPromptBuilder / tool-handler dependencies.
    private sealed class TestAgentLogic(IConversationStore store) : IAgentLogic
    {
        public string GetSystemPrompt(string conversationId, string source, ConversationType type, IReadOnlyList<string>? activeSkills = null, string? intention = null) => "";
        public IReadOnlyList<AgentToolDefinition> Tools => [];
        public Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default) => Task.FromResult("");
        public void AddMessage(string conversationId, Message message) => store.AddMessage(conversationId, message);
        public IReadOnlyList<Message> GetMessages(string conversationId, bool includeToolResultBlobs = false) => store.GetMessages(conversationId, includeToolResultBlobs);
        public Conversation? GetConversation(string conversationId) => store.Get(conversationId);
        public void UpdateConversation(Conversation conversation) => store.Update(conversation);

        public Task<bool> CompactAsync(string conversationId, CompactionReason reason, string? customInstructions = null, CancellationToken ct = default)
            => store.CompactNowAsync(conversationId, reason, customInstructions, ct);
    }
}
