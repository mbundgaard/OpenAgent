using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Per-call bridge between the Telnyx Media Streaming WebSocket and an <see cref="OpenAgent.Contracts.IVoiceSession"/>.
/// Stubbed in Task 17 — the read loop, write loop, barge-in handling, and thinking-clip pump
/// land in Tasks 18–22.
/// </summary>
public sealed class TelnyxMediaBridge : IDisposable
{
    private readonly TelnyxChannelProvider _provider;
    private readonly PendingBridge _pending;
    private readonly WebSocket _ws;
    private readonly ILogger<TelnyxMediaBridge> _logger;
    private readonly CancellationToken _ct;

    public TelnyxMediaBridge(TelnyxChannelProvider provider, PendingBridge pending, WebSocket ws,
        ILogger<TelnyxMediaBridge> logger, CancellationToken ct)
    {
        _provider = provider;
        _pending = pending;
        _ws = ws;
        _logger = logger;
        _ct = ct;
    }

    public async Task RunAsync()
    {
        // Stubbed in Task 17 — Tasks 18-22 fill this in.
        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stub", CancellationToken.None);
    }

    public void Dispose() { }
}
