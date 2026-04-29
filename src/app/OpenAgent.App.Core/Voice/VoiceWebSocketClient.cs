using System.Net.WebSockets;
using System.Text;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Core.Voice;

/// <summary>Owns one ClientWebSocket per call. Registered transient — view-model resolves a fresh instance per session attempt.</summary>
public sealed class VoiceWebSocketClient : IVoiceWebSocketClient
{
    private readonly ICredentialStore _credentials;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;

    public VoiceWebSocketClient(ICredentialStore credentials) => _credentials = credentials;

    public async Task ConnectAsync(string conversationId, CancellationToken ct)
    {
        var creds = await _credentials.LoadAsync(ct) ?? throw new InvalidOperationException("No credentials");
        var baseUri = new Uri(creds.BaseUrl);
        var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
        var wsUrl = new UriBuilder($"{scheme}://{baseUri.Authority}{baseUri.AbsolutePath.TrimEnd('/')}/ws/conversations/{Uri.EscapeDataString(conversationId)}/voice")
        {
            Query = $"api_key={Uri.EscapeDataString(creds.Token)}"
        }.Uri;

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(wsUrl, ct);
    }

    /// <summary>
    /// Sends one PCM16 audio chunk. Must serialize sends — ClientWebSocket throws
    /// InvalidOperationException on concurrent SendAsync calls, and the audio capture tap
    /// can fire frequently from a render thread.
    /// </summary>
    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(pcm16, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Reads frames until the WebSocket is closed or the token is cancelled.
    /// Emits a final <see cref="VoiceFrame.Disconnected"/> with AuthRejected set when the close code is 1008 or 4001.</summary>
    public async IAsyncEnumerable<VoiceFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_ws is null) yield break;
        var buffer = new byte[16 * 1024];
        var assembly = new MemoryStream();

        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            string? receiveError = null;
            try { result = await _ws.ReceiveAsync(buffer, ct); }
            catch (WebSocketException ex)
            {
                receiveError = ex.Message;
                result = null!;
            }

            if (receiveError is not null)
            {
                yield return new VoiceFrame.Disconnected(receiveError, AuthRejected: false);
                yield break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                var auth = _ws.CloseStatus is (WebSocketCloseStatus)1008 or (WebSocketCloseStatus)4001;
                yield return new VoiceFrame.Disconnected(_ws.CloseStatusDescription, auth);
                yield break;
            }

            assembly.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var bytes = assembly.ToArray();
            assembly.SetLength(0);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                yield return new VoiceFrame.AudioFrame(bytes);
            }
            else
            {
                var json = Encoding.UTF8.GetString(bytes);
                var evt = VoiceEventParser.Parse(json);
                if (evt is not null) yield return new VoiceFrame.EventFrame(evt);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var ws = _ws;
        _ws = null;
        if (ws is null) return;
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch { }
        ws.Dispose();
        _sendLock.Dispose();
    }
}
