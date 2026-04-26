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
        reg.Register("conv-1", fake);
        Assert.True(reg.TryGet("conv-1", out var got));
        Assert.Same(fake, got);
    }

    [Fact]
    public void Unregister_Removes()
    {
        var reg = new TelnyxBridgeRegistry();
        var fake = new object();
        reg.Register("conv-1", fake);
        reg.Unregister("conv-1");
        Assert.False(reg.TryGet("conv-1", out _));
    }

    [Fact]
    public void TryGet_Unknown_ReturnsFalse()
    {
        var reg = new TelnyxBridgeRegistry();
        Assert.False(reg.TryGet("missing", out _));
    }
}
