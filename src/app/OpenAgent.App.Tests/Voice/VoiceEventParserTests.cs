using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.Tests.Voice;

public class VoiceEventParserTests
{
    [Fact]
    public void Parses_session_ready()
    {
        var json = """{"type":"session_ready","input_sample_rate":24000,"output_sample_rate":24000,"input_codec":"pcm16","output_codec":"pcm16"}""";
        var evt = VoiceEventParser.Parse(json);
        var ready = Assert.IsType<VoiceEvent.SessionReady>(evt);
        Assert.Equal(24000, ready.InputSampleRate);
        Assert.Equal("pcm16", ready.InputCodec);
    }

    [Theory]
    [InlineData("speech_started", typeof(VoiceEvent.SpeechStarted))]
    [InlineData("speech_stopped", typeof(VoiceEvent.SpeechStopped))]
    [InlineData("audio_done", typeof(VoiceEvent.AudioDone))]
    [InlineData("thinking_started", typeof(VoiceEvent.ThinkingStarted))]
    [InlineData("thinking_stopped", typeof(VoiceEvent.ThinkingStopped))]
    public void Parses_simple_signals(string type, Type expected)
    {
        var evt = VoiceEventParser.Parse($"{{\"type\":\"{type}\"}}");
        Assert.IsType(expected, evt);
    }

    [Fact]
    public void Parses_error()
    {
        var evt = VoiceEventParser.Parse("""{"type":"error","message":"boom"}""");
        var e = Assert.IsType<VoiceEvent.Error>(evt);
        Assert.Equal("boom", e.Message);
    }

    [Fact]
    public void Unknown_type_returns_null()
    {
        Assert.Null(VoiceEventParser.Parse("""{"type":"future_thing"}"""));
    }
}
