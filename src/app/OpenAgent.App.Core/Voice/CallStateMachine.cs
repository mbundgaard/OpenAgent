namespace OpenAgent.App.Core.Voice;

/// <summary>Pure state machine for the call screen — no I/O, fully unit-tested.</summary>
public sealed class CallStateMachine
{
    public CallState State { get; private set; } = CallState.Idle;

    public void OnConnecting() => State = CallState.Connecting;
    public void OnReconnecting() => State = CallState.Reconnecting;
    public void OnEnded() => State = CallState.Ended;

    public void OnAudioReceived() => State = CallState.AssistantSpeaking;

    public void Apply(VoiceEvent evt)
    {
        switch (evt)
        {
            case VoiceEvent.SessionReady: State = CallState.Listening; break;
            case VoiceEvent.SpeechStarted: State = CallState.UserSpeaking; break;
            case VoiceEvent.SpeechStopped: State = CallState.Thinking; break;
            case VoiceEvent.AudioDone: State = CallState.Listening; break;
        }
    }
}
