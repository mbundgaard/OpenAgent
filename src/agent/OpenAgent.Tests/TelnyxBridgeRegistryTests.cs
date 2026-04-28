using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxBridgeRegistryTests
{
    [Fact]
    public void Register_Then_TryGet_Returns_The_Bridge()
    {
        var reg = new TelnyxBridgeRegistry();
        var fake = new object();
        reg.Register("call-1", "conv-1", fake);
        Assert.True(reg.TryGet("conv-1", out var got));
        Assert.Same(fake, got);
        Assert.True(reg.TryGetByCallControlId("call-1", out var byCall));
        Assert.Same(fake, byCall);
    }

    [Fact]
    public void Unregister_Removes_Both_Indices()
    {
        var reg = new TelnyxBridgeRegistry();
        var fake = new object();
        reg.Register("call-1", "conv-1", fake);
        reg.Unregister("call-1", "conv-1");
        Assert.False(reg.TryGet("conv-1", out _));
        Assert.False(reg.TryGetByCallControlId("call-1", out _));
    }

    [Fact]
    public void TryGet_Unknown_ReturnsFalse()
    {
        var reg = new TelnyxBridgeRegistry();
        Assert.False(reg.TryGet("missing", out _));
        Assert.False(reg.TryGetByCallControlId("missing", out _));
    }

    [Fact]
    public void UpdateConversationId_ReKeys_The_Conversation_Index()
    {
        var reg = new TelnyxBridgeRegistry();
        var fake = new object();
        reg.Register("call-1", "throwaway", fake);

        reg.UpdateConversationId("call-1", "throwaway", "extension");

        Assert.False(reg.TryGet("throwaway", out _));
        Assert.True(reg.TryGet("extension", out var got));
        Assert.Same(fake, got);
        // Call-control index unchanged
        Assert.True(reg.TryGetByCallControlId("call-1", out var byCall));
        Assert.Same(fake, byCall);
    }

    [Fact]
    public void UpdateConversationId_Unknown_CallControl_Is_NoOp()
    {
        var reg = new TelnyxBridgeRegistry();
        reg.UpdateConversationId("missing", "old", "new");
        Assert.False(reg.TryGet("new", out _));
    }
}
