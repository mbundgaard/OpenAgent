using System.Text.Json;
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxMediaFrameTests
{
    [Fact]
    public void Parse_StartEvent()
    {
        var json = """{"event":"start","sequence_number":"1","start":{"call_control_id":"call-123","client_state":"YwAxMjM=","media_format":{"encoding":"PCMU","sample_rate":8000,"channels":1}},"stream_id":"s1"}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("start", frame.Event);
        Assert.Equal("call-123", frame.Start!.CallControlId);
        Assert.Equal("PCMU", frame.Start.MediaFormat.Encoding);
        Assert.Equal(8000, frame.Start.MediaFormat.SampleRate);
    }

    [Fact]
    public void Parse_MediaEvent_InboundTrack()
    {
        var json = """{"event":"media","sequence_number":"4","media":{"track":"inbound","chunk":"2","timestamp":"123","payload":"AAEC"}}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("media", frame.Event);
        Assert.Equal("inbound", frame.Media!.Track);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02 }, frame.Media.PayloadBytes);
    }

    [Fact]
    public void Parse_MediaEvent_OutboundTrack_ParsesButCallerShouldFilter()
    {
        var json = """{"event":"media","media":{"track":"outbound","payload":"AA=="}}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("outbound", frame.Media!.Track);
    }

    [Fact]
    public void Parse_StopEvent()
    {
        var json = """{"event":"stop","stop":{"reason":"hangup"}}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("stop", frame.Event);
    }

    [Fact]
    public void Parse_DtmfEvent_ParsedNotIgnored()
    {
        var json = """{"event":"dtmf","dtmf":{"digit":"5"}}""";
        var frame = TelnyxMediaFrame.Parse(json);
        Assert.Equal("dtmf", frame.Event);
        Assert.Equal("5", frame.Dtmf!.Digit);
    }

    [Fact]
    public void Compose_MediaFrame_EncodesPayload()
    {
        var json = TelnyxMediaFrame.ComposeMedia(new byte[] { 0xff, 0xfe });
        var doc = JsonDocument.Parse(json);
        Assert.Equal("media", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("//4=", doc.RootElement.GetProperty("media").GetProperty("payload").GetString());
    }

    [Fact]
    public void Compose_ClearFrame()
    {
        var json = TelnyxMediaFrame.ComposeClear();
        Assert.Equal("""{"event":"clear"}""", json);
    }
}
