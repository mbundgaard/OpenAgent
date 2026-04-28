using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

/// <summary>
/// Tests for <see cref="EndCallTool"/> — bridge-presence gating and the pending-hangup poke.
/// The tool must NOT issue any Telnyx hangup itself; it just sets the pending flag on the
/// active bridge so the bridge's hangup state machine can drain the farewell audio first.
/// </summary>
public class TelnyxEndCallToolTests
{
    [Fact]
    public async Task NoActiveBridge_ReturnsError()
    {
        var registry = new TelnyxBridgeRegistry();

        var tool = new EndCallTool(registry);
        var result = await tool.ExecuteAsync("{}", "conv-2");

        Assert.Contains("no active call", result, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("error", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveBridge_SetsPendingHangup_AndReturnsOk()
    {
        var registry = new TelnyxBridgeRegistry();
        var fake = new FakeBridge();
        registry.Register("call-3", "conv-3", fake);

        var tool = new EndCallTool(registry);
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
        public List<string> DtmfDigits { get; } = new();
        public void SetPendingHangup() => HangupRequested = true;
        public void OnDtmfReceived(string digit) => DtmfDigits.Add(digit);
    }
}
