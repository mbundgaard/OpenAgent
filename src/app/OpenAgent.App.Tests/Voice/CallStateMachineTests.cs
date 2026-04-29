using OpenAgent.App.Core.Voice;

namespace OpenAgent.App.Tests.Voice;

public class CallStateMachineTests
{
    [Fact]
    public void Starts_idle()
        => Assert.Equal(CallState.Idle, new CallStateMachine().State);

    [Fact]
    public void Connecting_to_listening_via_session_ready()
    {
        var sm = new CallStateMachine();
        sm.OnConnecting();
        Assert.Equal(CallState.Connecting, sm.State);
        sm.Apply(new VoiceEvent.SessionReady(24000, 24000, "pcm16", "pcm16"));
        Assert.Equal(CallState.Listening, sm.State);
    }

    [Fact]
    public void Speech_started_means_user_speaking()
    {
        var sm = new CallStateMachine();
        sm.OnConnecting();
        sm.Apply(new VoiceEvent.SessionReady(24000, 24000, "pcm16", "pcm16"));
        sm.Apply(new VoiceEvent.SpeechStarted());
        Assert.Equal(CallState.UserSpeaking, sm.State);
    }

    [Fact]
    public void Speech_stopped_then_audio_done_listens_again()
    {
        var sm = Bootstrap();
        sm.Apply(new VoiceEvent.SpeechStarted());
        sm.Apply(new VoiceEvent.SpeechStopped());
        Assert.Equal(CallState.Thinking, sm.State);
        sm.Apply(new VoiceEvent.AudioDone());
        Assert.Equal(CallState.Listening, sm.State);
    }

    [Fact]
    public void Audio_received_marks_assistant_speaking()
    {
        var sm = Bootstrap();
        sm.OnAudioReceived();
        Assert.Equal(CallState.AssistantSpeaking, sm.State);
    }

    [Fact]
    public void Disconnect_marks_reconnecting()
    {
        var sm = Bootstrap();
        sm.OnReconnecting();
        Assert.Equal(CallState.Reconnecting, sm.State);
    }

    private static CallStateMachine Bootstrap()
    {
        var sm = new CallStateMachine();
        sm.OnConnecting();
        sm.Apply(new VoiceEvent.SessionReady(24000, 24000, "pcm16", "pcm16"));
        return sm;
    }
}
