using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests;

public class WhatsAppNodeProcessTests
{
    [Fact]
    public void ParseLine_QrMessage_ReturnsQrEvent()
    {
        var evt = WhatsAppNodeProcess.ParseLine("{\"type\":\"qr\",\"data\":\"2@AbC123\"}");
        Assert.NotNull(evt);
        Assert.Equal("qr", evt.Type);
        Assert.Equal("2@AbC123", evt.Data);
    }

    [Fact]
    public void ParseLine_ConnectedMessage_ReturnsConnectedEvent()
    {
        var evt = WhatsAppNodeProcess.ParseLine("{\"type\":\"connected\",\"jid\":\"+45@s.whatsapp.net\"}");
        Assert.NotNull(evt);
        Assert.Equal("connected", evt.Type);
        Assert.Equal("+45@s.whatsapp.net", evt.Jid);
    }

    [Fact]
    public void ParseLine_MessageEvent_ParsesAllFields()
    {
        var json = "{\"type\":\"message\",\"id\":\"ABC\",\"chatId\":\"+45@s.whatsapp.net\",\"from\":\"+45\",\"pushName\":\"Alice\",\"text\":\"Hello\",\"timestamp\":1711180800}";
        var evt = WhatsAppNodeProcess.ParseLine(json);
        Assert.NotNull(evt);
        Assert.Equal("message", evt.Type);
        Assert.Equal("ABC", evt.Id);
        Assert.Equal("+45@s.whatsapp.net", evt.ChatId);
        Assert.Equal("+45", evt.From);
        Assert.Equal("Alice", evt.PushName);
        Assert.Equal("Hello", evt.Text);
        Assert.Equal(1711180800, evt.Timestamp);
    }

    [Fact]
    public void ParseLine_DisconnectedEvent_IncludesReason()
    {
        var evt = WhatsAppNodeProcess.ParseLine("{\"type\":\"disconnected\",\"reason\":\"loggedOut\"}");
        Assert.NotNull(evt);
        Assert.Equal("disconnected", evt.Type);
        Assert.Equal("loggedOut", evt.Reason);
    }

    [Fact]
    public void ParseLine_PongEvent_Parsed()
    {
        var evt = WhatsAppNodeProcess.ParseLine("{\"type\":\"pong\"}");
        Assert.NotNull(evt);
        Assert.Equal("pong", evt.Type);
    }

    [Fact]
    public void ParseLine_ErrorEvent_IncludesMessage()
    {
        var evt = WhatsAppNodeProcess.ParseLine("{\"type\":\"error\",\"message\":\"auth failed\"}");
        Assert.NotNull(evt);
        Assert.Equal("error", evt.Type);
        Assert.Equal("auth failed", evt.Message);
    }

    [Fact]
    public void ParseLine_InvalidJson_ReturnsNull()
    {
        var evt = WhatsAppNodeProcess.ParseLine("not json");
        Assert.Null(evt);
    }

    [Fact]
    public void ParseLine_EmptyLine_ReturnsNull()
    {
        var evt = WhatsAppNodeProcess.ParseLine("");
        Assert.Null(evt);
    }

    [Fact]
    public void FormatSendCommand_ProducesValidJson()
    {
        var json = WhatsAppNodeProcess.FormatSendCommand("+45@s.whatsapp.net", "Hello");
        Assert.Contains("\"type\":\"send\"", json);
        Assert.Contains("\"chatId\":\"+45@s.whatsapp.net\"", json);
        Assert.Contains("\"text\":\"Hello\"", json);
    }

    [Fact]
    public void FormatComposingCommand_ProducesValidJson()
    {
        var json = WhatsAppNodeProcess.FormatComposingCommand("+45@s.whatsapp.net");
        Assert.Contains("\"type\":\"composing\"", json);
        Assert.Contains("\"chatId\":\"+45@s.whatsapp.net\"", json);
    }

    [Fact]
    public void FormatPingCommand_ProducesValidJson()
    {
        var json = WhatsAppNodeProcess.FormatPingCommand();
        Assert.Contains("\"type\":\"ping\"", json);
    }

    [Fact]
    public void FormatShutdownCommand_ProducesValidJson()
    {
        var json = WhatsAppNodeProcess.FormatShutdownCommand();
        Assert.Contains("\"type\":\"shutdown\"", json);
    }

    [Fact]
    public void ParseLine_MessageEventWithReplyTo_ParsesReplyToField()
    {
        var json = "{\"type\":\"message\",\"id\":\"ABC\",\"chatId\":\"+45@s.whatsapp.net\",\"text\":\"got it\",\"replyTo\":\"XYZ\"}";
        var evt = WhatsAppNodeProcess.ParseLine(json);
        Assert.NotNull(evt);
        Assert.Equal("ABC", evt.Id);
        Assert.Equal("XYZ", evt.ReplyTo);
    }

    [Fact]
    public void ParseLine_MessageEventWithoutReplyTo_HasNullReplyTo()
    {
        var json = "{\"type\":\"message\",\"id\":\"ABC\",\"chatId\":\"+45@s.whatsapp.net\",\"text\":\"hi\"}";
        var evt = WhatsAppNodeProcess.ParseLine(json);
        Assert.NotNull(evt);
        Assert.Null(evt.ReplyTo);
    }
}
