using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Connection state for the WhatsApp channel.
/// </summary>
public enum WhatsAppConnectionState
{
    /// <summary>No credentials — waiting for QR scan.</summary>
    Unpaired,

    /// <summary>QR code generated — waiting for the user to scan.</summary>
    Pairing,

    /// <summary>Authenticated and connected to WhatsApp.</summary>
    Connected,

    /// <summary>Reconnection attempts exhausted.</summary>
    Failed
}

/// <summary>
/// Channel provider that connects the agent to WhatsApp via a Node.js Baileys bridge process.
/// Manages process lifecycle, QR pairing state machine, ping/pong health monitoring,
/// and exponential-backoff reconnection.
/// </summary>
public sealed class WhatsAppChannelProvider : IChannelProvider, IAsyncDisposable
{
    private readonly WhatsAppOptions _options;
    private readonly string _connectionId;
    private readonly string _authDir;
    private readonly IConversationStore _store;
    private readonly ILlmTextProvider _textProvider;
    private readonly string _providerKey;
    private readonly string _model;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WhatsAppChannelProvider> _logger;

    private readonly object _lock = new();
    private WhatsAppConnectionState _state = WhatsAppConnectionState.Unpaired;
    private WhatsAppNodeProcess? _nodeProcess;
    private readonly WhatsAppMessageHandler _handler;
    private WhatsAppNodeProcessSender? _sender;
    private string? _latestQr;
    private TaskCompletionSource<string?>? _qrReady;
    private Timer? _pingTimer;
    private DateTime _lastPongTime = DateTime.UtcNow;
    private int _reconnectAttempts;
    private DateTime? _lastConnectedAt;
    private string? _lastError;

    /// <summary>Current connection state.</summary>
    public WhatsAppConnectionState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>
    /// Creates a new WhatsAppChannelProvider.
    /// </summary>
    /// <param name="options">WhatsApp channel configuration (allowlist, etc.).</param>
    /// <param name="connectionId">Unique connection identifier.</param>
    /// <param name="authDir">Directory for WhatsApp authentication state files.</param>
    /// <param name="store">Conversation store for persistence.</param>
    /// <param name="textProvider">LLM text provider for completions.</param>
    /// <param name="providerKey">Provider key (e.g. "azure-openai-text").</param>
    /// <param name="model">Model name (e.g. "gpt-5.2-chat").</param>
    /// <param name="loggerFactory">Logger factory for creating typed loggers.</param>
    public WhatsAppChannelProvider(
        WhatsAppOptions options,
        string connectionId,
        string authDir,
        IConversationStore store,
        ILlmTextProvider textProvider,
        string providerKey,
        string model,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _connectionId = connectionId;
        _authDir = authDir;
        _store = store;
        _textProvider = textProvider;
        _providerKey = providerKey;
        _model = model;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WhatsAppChannelProvider>();

        // Create the message handler -- it handles dedup, access control, LLM calls
        _handler = new WhatsAppMessageHandler(
            store, textProvider, connectionId, providerKey, model, options,
            loggerFactory.CreateLogger<WhatsAppMessageHandler>());
    }

    /// <summary>
    /// Starts the WhatsApp channel. If auth credentials exist, starts the Node.js bridge
    /// immediately. Otherwise waits in Unpaired state until pairing is initiated.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        // Ensure auth directory exists
        Directory.CreateDirectory(_authDir);

        // Check if credentials exist (any files in the auth dir)
        var hasCredentials = Directory.EnumerateFiles(_authDir).Any();

        if (hasCredentials)
        {
            _logger.LogInformation("WhatsApp [{ConnectionId}]: credentials found, starting bridge", _connectionId);
            await StartNodeProcessAsync(ct);
        }
        else
        {
            _logger.LogInformation("WhatsApp [{ConnectionId}]: no credentials, waiting for QR pairing", _connectionId);
            lock (_lock) _state = WhatsAppConnectionState.Unpaired;
        }
    }

    /// <summary>
    /// Stops the WhatsApp channel: cancels the ping timer and shuts down the Node.js bridge.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _pingTimer?.Dispose();
        _pingTimer = null;

        if (_nodeProcess is not null)
        {
            await _nodeProcess.StopAsync();
        }

        _logger.LogInformation("WhatsApp [{ConnectionId}]: stopped", _connectionId);
    }

    /// <summary>
    /// Initiates the QR pairing flow. Starts the Node.js bridge if not already running.
    /// No-op if already pairing or connected.
    /// </summary>
    public async Task StartPairingAsync()
    {
        lock (_lock)
        {
            if (_state is WhatsAppConnectionState.Pairing or WhatsAppConnectionState.Connected)
                return;

            _qrReady = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _state = WhatsAppConnectionState.Pairing;
        }

        _logger.LogInformation("WhatsApp [{ConnectionId}]: starting pairing", _connectionId);
        await StartNodeProcessAsync(CancellationToken.None);
    }

    /// <summary>
    /// Returns the current connection state and QR code data (if available).
    /// Initiates pairing if currently unpaired. Waits up to <paramref name="timeout"/>
    /// for a QR code to become available.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for QR data.</param>
    /// <returns>Tuple of current state, QR data, and last error (if any).</returns>
    public async Task<(WhatsAppConnectionState Status, string? QrData, string? Error)> GetQrAsync(TimeSpan timeout)
    {
        // Already connected -- nothing to pair
        lock (_lock)
        {
            if (_state == WhatsAppConnectionState.Connected)
                return (WhatsAppConnectionState.Connected, null, null);
        }

        // If unpaired, kick off pairing
        lock (_lock)
        {
            if (_state == WhatsAppConnectionState.Unpaired)
            {
                // Release lock before async call -- StartPairingAsync will re-acquire
            }
        }

        if (State == WhatsAppConnectionState.Unpaired)
            await StartPairingAsync();

        // If we already have a QR code, return it immediately
        lock (_lock)
        {
            if (_latestQr is not null)
                return (WhatsAppConnectionState.Pairing, _latestQr, null);
        }

        // Wait for QR code with timeout
        TaskCompletionSource<string?>? tcs;
        lock (_lock)
        {
            tcs = _qrReady;
        }

        if (tcs is not null)
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            if (completed == tcs.Task)
            {
                // QR ready
            }
        }

        lock (_lock)
        {
            return (_state, _latestQr, _lastError);
        }
    }

    /// <summary>
    /// Disposes the provider: stops the ping timer and the Node.js bridge process.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _pingTimer?.Dispose();
        if (_nodeProcess is not null)
            await _nodeProcess.DisposeAsync();
    }

    /// <summary>
    /// Starts the Node.js Baileys bridge child process and wires up event handling and ping timer.
    /// </summary>
    private async Task StartNodeProcessAsync(CancellationToken ct)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "node", "baileys-bridge.js");

        _nodeProcess = new WhatsAppNodeProcess(scriptPath, _loggerFactory.CreateLogger<WhatsAppNodeProcess>());
        _nodeProcess.OnEvent = HandleNodeEvent;
        await _nodeProcess.StartAsync(_authDir, ct);

        // Create sender backed by the node process
        _sender = new WhatsAppNodeProcessSender(_nodeProcess);

        // Start ping timer -- fires every 60 seconds
        _pingTimer?.Dispose();
        _pingTimer = new Timer(PingTimerCallback, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        _lastPongTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Handles events from the Node.js bridge: QR codes, connection state, messages, errors.
    /// </summary>
    private void HandleNodeEvent(NodeEvent evt)
    {
        switch (evt.Type)
        {
            case "qr":
                lock (_lock)
                {
                    _latestQr = evt.Data;
                    _state = WhatsAppConnectionState.Pairing;

                    // Complete any pending QR wait
                    if (_qrReady is not null)
                    {
                        _qrReady.TrySetResult(evt.Data);
                        _qrReady = null;
                    }
                }
                _logger.LogInformation("WhatsApp [{ConnectionId}]: QR code received", _connectionId);
                break;

            case "connected":
                lock (_lock)
                {
                    _state = WhatsAppConnectionState.Connected;
                    _latestQr = null;
                    _lastError = null;
                    _lastConnectedAt = DateTime.UtcNow;
                    _reconnectAttempts = 0;
                }
                _logger.LogInformation("WhatsApp [{ConnectionId}]: connected (jid={Jid})", _connectionId, evt.Jid);
                break;

            case "message":
                // Fire-and-forget -- don't block the event loop
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _handler.HandleMessageAsync(_sender!, evt, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "WhatsApp [{ConnectionId}]: error handling message", _connectionId);
                    }
                });
                break;

            case "disconnected":
                if (string.Equals(evt.Reason, "loggedOut", StringComparison.OrdinalIgnoreCase))
                {
                    // User logged out -- clear credentials and go to unpaired
                    _logger.LogWarning("WhatsApp [{ConnectionId}]: logged out, clearing auth", _connectionId);

                    lock (_lock) _state = WhatsAppConnectionState.Unpaired;

                    // Delete auth dir contents
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(_authDir))
                            File.Delete(file);
                        foreach (var dir in Directory.EnumerateDirectories(_authDir))
                            Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "WhatsApp [{ConnectionId}]: error clearing auth dir", _connectionId);
                    }

                    // Stop the process -- do NOT retry
                    _ = Task.Run(async () =>
                    {
                        try { if (_nodeProcess is not null) await _nodeProcess.StopAsync(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "WhatsApp [{ConnectionId}]: error stopping after logout", _connectionId); }
                    });
                }
                else
                {
                    // Unexpected disconnect -- schedule reconnect
                    _logger.LogWarning("WhatsApp [{ConnectionId}]: disconnected (reason={Reason}), scheduling reconnect",
                        _connectionId, evt.Reason);
                    _ = Task.Run(ScheduleReconnectAsync);
                }
                break;

            case "pong":
                _lastPongTime = DateTime.UtcNow;
                break;

            case "error":
                lock (_lock) _lastError = evt.Message;
                _logger.LogError("WhatsApp [{ConnectionId}]: bridge error: {Message}", _connectionId, evt.Message);
                break;

            default:
                _logger.LogDebug("WhatsApp [{ConnectionId}]: unhandled event type={Type}", _connectionId, evt.Type);
                break;
        }
    }

    /// <summary>
    /// Reconnects with exponential backoff: 2s base, 1.5x multiplier, capped at 30s, max 10 attempts.
    /// Resets attempt counter if the previous session was connected for more than 60 seconds.
    /// </summary>
    private async Task ScheduleReconnectAsync()
    {
        // If we were connected long enough, reset attempt counter
        lock (_lock)
        {
            if (_lastConnectedAt.HasValue &&
                (DateTime.UtcNow - _lastConnectedAt.Value).TotalSeconds > 60)
            {
                _reconnectAttempts = 0;
            }
        }

        int attempt;
        lock (_lock)
        {
            if (_reconnectAttempts >= 10)
            {
                _state = WhatsAppConnectionState.Failed;
                _logger.LogError("WhatsApp [{ConnectionId}]: reconnect attempts exhausted, giving up", _connectionId);
                return;
            }

            attempt = _reconnectAttempts;
            _reconnectAttempts++;
        }

        var delayMs = (int)Math.Min(2000 * Math.Pow(1.5, attempt), 30000);
        _logger.LogInformation("WhatsApp [{ConnectionId}]: reconnecting in {Delay}ms (attempt {Attempt})",
            _connectionId, delayMs, attempt + 1);

        await Task.Delay(delayMs);

        try
        {
            // Stop old process if still around
            if (_nodeProcess is not null)
            {
                try { await _nodeProcess.StopAsync(); }
                catch { /* best effort */ }
            }

            await StartNodeProcessAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp [{ConnectionId}]: reconnect failed", _connectionId);
            // Schedule another attempt
            _ = Task.Run(ScheduleReconnectAsync);
        }
    }

    /// <summary>
    /// Ping timer callback. Sends a ping to the bridge and checks for stale pong.
    /// If no pong received within tolerance (70s), forces a restart.
    /// </summary>
    private void PingTimerCallback(object? state)
    {
        lock (_lock)
        {
            if (_state != WhatsAppConnectionState.Connected)
                return;
        }

        // Send ping
        _ = Task.Run(async () =>
        {
            try
            {
                if (_nodeProcess is not null)
                    await _nodeProcess.WriteAsync(WhatsAppNodeProcess.FormatPingCommand());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WhatsApp [{ConnectionId}]: failed to send ping", _connectionId);
            }
        });

        // Check pong staleness -- 60s interval + 10s tolerance = 70s
        var sincePong = DateTime.UtcNow - _lastPongTime;
        if (sincePong.TotalSeconds > 70)
        {
            _logger.LogWarning("WhatsApp [{ConnectionId}]: no pong for {Seconds}s, forcing restart",
                _connectionId, (int)sincePong.TotalSeconds);
            _ = Task.Run(ScheduleReconnectAsync);
        }
    }

    /// <summary>
    /// Inner sender implementation that delegates to the Node.js bridge process.
    /// </summary>
    private sealed class WhatsAppNodeProcessSender : IWhatsAppSender
    {
        private readonly WhatsAppNodeProcess _process;

        public WhatsAppNodeProcessSender(WhatsAppNodeProcess process) => _process = process;

        /// <summary>Sends a composing indicator to the specified chat.</summary>
        public Task SendComposingAsync(string chatId) =>
            _process.WriteAsync(WhatsAppNodeProcess.FormatComposingCommand(chatId));

        /// <summary>Sends a text message to the specified chat.</summary>
        public Task SendTextAsync(string chatId, string text) =>
            _process.WriteAsync(WhatsAppNodeProcess.FormatSendCommand(chatId, text));
    }
}
