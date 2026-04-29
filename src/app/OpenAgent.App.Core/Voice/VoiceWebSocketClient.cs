using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Core.Voice;

/// <summary>Owns one ClientWebSocket per call. Registered transient — view-model resolves a fresh instance per session attempt.</summary>
public sealed class VoiceWebSocketClient : IVoiceWebSocketClient
{
    private readonly ICredentialStore _credentials;
    private readonly ILogger<VoiceWebSocketClient> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private int _sendCount;

    public VoiceWebSocketClient(ICredentialStore credentials, ILogger<VoiceWebSocketClient>? logger = null)
    {
        _credentials = credentials;
        _logger = logger ?? NullLogger<VoiceWebSocketClient>.Instance;
    }

    public async Task ConnectAsync(string conversationId, CancellationToken ct)
    {
        var creds = await _credentials.LoadAsync(ct) ?? throw new InvalidOperationException("No credentials");
        var baseUri = new Uri(creds.BaseUrl);
        var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
        var wsUrl = new UriBuilder($"{scheme}://{baseUri.Authority}{baseUri.AbsolutePath.TrimEnd('/')}/ws/conversations/{Uri.EscapeDataString(conversationId)}/voice")
        {
            Query = $"api_key={Uri.EscapeDataString(creds.Token)}"
        }.Uri;

        // Log host + conversation only — never the token or the full query string.
        _logger.LogInformation("WS connect {Scheme}://{Host}{Path} convo={ConversationId}",
            scheme, baseUri.Authority, wsUrl.AbsolutePath, conversationId);

        _ws = new ClientWebSocket();
        try
        {
            await _ws.ConnectAsync(wsUrl, ct);
            _logger.LogInformation("WS connected state={State}", _ws.State);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("WS connect failed: {Error}", ex.Message);
            throw;
        }
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
            // Log only every ~100th frame so we get a heartbeat without flooding the buffer.
            // Audio captured at 24kHz mono PCM16 in ~85ms chunks = ~12 sends/sec.
            var n = Interlocked.Increment(ref _sendCount);
            if (n == 1 || n % 100 == 0) _logger.LogDebug("WS audio frames sent: {Count}", n);
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
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                receiveError = ex.Message;
                result = null!;
            }

            if (receiveError is not null)
            {
                _logger.LogWarning("WS receive error: {Error}", receiveError);
                yield return new VoiceFrame.Disconnected(receiveError, AuthRejected: false);
                yield break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                var auth = _ws.CloseStatus is (WebSocketCloseStatus)1008 or (WebSocketCloseStatus)4001;
                _logger.LogInformation("WS closed status={Status} ({Code}) auth={Auth} desc={Desc}",
                    _ws.CloseStatus, (int?)_ws.CloseStatus, auth, _ws.CloseStatusDescription ?? "");
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
                if (evt is not null)
                {
                    _logger.LogDebug("WS event {EventType}", evt.GetType().Name);
                    yield return new VoiceFrame.EventFrame(evt);
                }
                else
                {
                    // Unparsed text frames are silent today; log them so we notice protocol drift.
                    _logger.LogDebug("WS text frame unparsed (len={Len})", bytes.Length);
                }
            }
        }

        if (_ws is not null && _ws.State != WebSocketState.Open)
            _logger.LogWarning("WS loop exited state={State} (was not a clean close)", _ws.State);
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
