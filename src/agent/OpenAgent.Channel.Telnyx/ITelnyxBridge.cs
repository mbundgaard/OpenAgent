namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Minimal surface the webhook handler and EndCallTool need from a registered bridge — exists so
/// the tool/handler can invoke without taking a hard dependency on TelnyxMediaBridge (also enables
/// straightforward unit tests with a fake bridge).
/// </summary>
public interface ITelnyxBridge
{
    void SetPendingHangup();

    /// <summary>
    /// Forward a DTMF digit (from <c>call.dtmf.received</c>) into the bridge. Inside the
    /// bridge's 8-second extension-routing window the first digit triggers a conversation swap;
    /// outside the window the digit is logged and ignored.
    /// </summary>
    void OnDtmfReceived(string digit);
}
