namespace OpenAgent.App.Core.Voice;

/// <summary>Per-call WebSocket bridge to the agent's /ws/conversations/{id}/voice endpoint.</summary>
public interface IVoiceWebSocketClient : IAsyncDisposable
{
    Task ConnectAsync(string conversationId, CancellationToken ct);
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);
    IAsyncEnumerable<VoiceFrame> ReadFramesAsync(CancellationToken ct);
}
