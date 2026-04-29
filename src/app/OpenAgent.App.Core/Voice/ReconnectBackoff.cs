namespace OpenAgent.App.Core.Voice;

/// <summary>Exponential backoff with a fixed cap. Attempts after maxTries set GiveUp=true.</summary>
public sealed class ReconnectBackoff
{
    private readonly int _maxTries;
    private int _attempt;

    public ReconnectBackoff(int maxTries = 5) => _maxTries = maxTries;

    public bool GiveUp => _attempt >= _maxTries;

    public TimeSpan NextDelay()
    {
        if (_attempt < _maxTries) _attempt++;
        var seconds = Math.Min(8, 1 << Math.Min(3, _attempt - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    public void Reset() => _attempt = 0;
}
