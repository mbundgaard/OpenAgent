using OpenAgent.Channel.Telnyx;
using OpenAgent.Models.Conversations;
using OpenAgent.Tests.Fakes;
using Xunit;

namespace OpenAgent.Tests;

/// <summary>
/// Tests for <see cref="EndCallTool"/> — phone-only gating, no-bridge handling, and the
/// pending-hangup poke. The tool must NOT issue any Telnyx hangup itself; it just sets the
/// pending flag on the active bridge so the bridge's hangup state machine can drain the
/// farewell audio first.
/// </summary>
public class TelnyxEndCallToolTests
{
    [Fact]
    public async Task NonPhoneConversation_ReturnsError()
    {
        var registry = new TelnyxBridgeRegistry();
        var store = new InMemoryConversationStore();
        // Voice (not Phone) — end_call must refuse since it's a phone-only operation.
        store.GetOrCreate("conv-1", "app", ConversationType.Voice, "openai", "gpt-4o");

        var tool = new EndCallTool(registry, store);
        var result = await tool.ExecuteAsync("{}", "conv-1");

        Assert.Contains("phone", result, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoActiveBridge_ReturnsError()
    {
        var registry = new TelnyxBridgeRegistry();
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv-2", "telnyx", ConversationType.Phone, "openai", "gpt-4o");

        var tool = new EndCallTool(registry, store);
        var result = await tool.ExecuteAsync("{}", "conv-2");

        Assert.Contains("no active call", result, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveBridge_SetsPendingHangup_AndReturnsOk()
    {
        var registry = new TelnyxBridgeRegistry();
        var store = new InMemoryConversationStore();
        store.GetOrCreate("conv-3", "telnyx", ConversationType.Phone, "openai", "gpt-4o");

        var fake = new FakeBridge();
        registry.Register("conv-3", fake);

        var tool = new EndCallTool(registry, store);
        var result = await tool.ExecuteAsync("{}", "conv-3");

        Assert.Equal("ok", result);
        Assert.True(fake.HangupRequested);
    }

    /// <summary>
    /// Stand-in for <see cref="TelnyxMediaBridge"/> — implements <see cref="ITelnyxBridge"/> so the
    /// tool can poke it without spinning up a real WebSocket / voice provider.
    /// </summary>
    private sealed class FakeBridge : ITelnyxBridge
    {
        public bool HangupRequested { get; private set; }
        public void SetPendingHangup() => HangupRequested = true;
    }
}
