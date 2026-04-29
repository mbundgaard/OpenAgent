namespace OpenAgent.App.Core.Voice;

/// <summary>Routes transcript_delta events into either "append a new bubble" or "grow the last bubble"
/// per the source-flip rule. Mirrors src/web/src/apps/chat/hooks/useVoiceSession.ts.</summary>
public sealed class TranscriptRouter
{
    private readonly Action<TranscriptSource, string> _onAppend;
    private readonly Action<string> _onUpdateLast;
    private TranscriptSource? _lastSource;
    private string _accumulated = "";

    public TranscriptRouter(Action<TranscriptSource, string> onAppend, Action<string> onUpdateLast)
    {
        _onAppend = onAppend;
        _onUpdateLast = onUpdateLast;
    }

    public void OnDelta(TranscriptSource source, string text)
    {
        if (_lastSource != source)
        {
            _lastSource = source;
            _accumulated = text;
            _onAppend(source, text);
        }
        else
        {
            _accumulated += text;
            _onUpdateLast(_accumulated);
        }
    }

    public void OnDone()
    {
        _lastSource = null;
        _accumulated = "";
    }
}
