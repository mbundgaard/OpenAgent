namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Minimal surface the EndCallTool needs from a registered bridge — exists solely so the tool can
/// invoke SetPendingHangup without taking a hard dependency on TelnyxMediaBridge (also enables
/// straightforward unit tests with a fake bridge).
/// </summary>
public interface ITelnyxBridge
{
    void SetPendingHangup();
}
