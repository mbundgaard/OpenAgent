using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Represents an event received from the Node.js Baileys bridge process via stdout JSON lines.
/// All properties except Type are optional — each event type populates a different subset.
/// </summary>
public sealed record NodeEvent
{
    /// <summary>Event type: qr, connected, message, disconnected, pong, error, etc.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>QR code data (for type=qr).</summary>
    [JsonPropertyName("data")]
    public string? Data { get; init; }

    /// <summary>WhatsApp JID (for type=connected).</summary>
    [JsonPropertyName("jid")]
    public string? Jid { get; init; }

    /// <summary>Message ID (for type=message).</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Correlation ID echoed back on <c>sent</c> events to match a request to its response.
    /// Null for inbound events.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    /// <summary>Chat ID / remote JID (for type=message).</summary>
    [JsonPropertyName("chatId")]
    public string? ChatId { get; init; }

    /// <summary>Sender phone number (for type=message).</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    /// <summary>Sender display name (for type=message).</summary>
    [JsonPropertyName("pushName")]
    public string? PushName { get; init; }

    /// <summary>Message text body (for type=message).</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>Unix timestamp in seconds (for type=message).</summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    /// <summary>The message ID this message is replying to (for type=message).
    /// Populated from Baileys extendedTextMessage.contextInfo.stanzaId; null when the
    /// message is not a reply.</summary>
    [JsonPropertyName("replyTo")]
    public string? ReplyTo { get; init; }

    /// <summary>Disconnect reason (for type=disconnected).</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Error message (for type=error).</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// Manages the Node.js Baileys bridge child process. Handles process lifecycle,
/// stdin/stdout JSON line protocol, and stderr logging.
/// </summary>
public sealed class WhatsAppNodeProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _scriptPath;
    private readonly ILogger<WhatsAppNodeProcess> _logger;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _stdinTask;
    private Channel<string>? _stdinChannel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pendingSends = new();
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Callback invoked for each parsed event from the Node.js process stdout.
    /// </summary>
    public Action<NodeEvent>? OnEvent { get; set; }

    /// <summary>
    /// Whether the child process is currently running.
    /// </summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Creates a new WhatsAppNodeProcess.
    /// </summary>
    /// <param name="scriptPath">Path to the baileys-bridge.js script.</param>
    /// <param name="logger">Logger instance.</param>
    public WhatsAppNodeProcess(string scriptPath, ILogger<WhatsAppNodeProcess> logger)
    {
        _scriptPath = scriptPath;
        _logger = logger;
    }

    /// <summary>
    /// Parses a single JSON line from the Node.js process stdout into a NodeEvent.
    /// Returns null for empty or invalid input.
    /// </summary>
    /// <param name="line">A JSON line from stdout.</param>
    /// <returns>Parsed NodeEvent, or null if the line is empty or invalid JSON.</returns>
    public static NodeEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            return JsonSerializer.Deserialize<NodeEvent>(line, ParseOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a send-message command as a JSON line for the Node.js process stdin.
    /// </summary>
    /// <param name="chatId">Target chat JID.</param>
    /// <param name="text">Message text to send.</param>
    /// <param name="correlationId">Correlation ID echoed back on the <c>sent</c> event for matching.</param>
    /// <returns>JSON string ready to write to stdin.</returns>
    public static string FormatSendCommand(string chatId, string text, string correlationId)
    {
        return JsonSerializer.Serialize(new { type = "send", chatId, text, correlationId }, WriteOptions);
    }

    /// <summary>
    /// Formats a composing (typing indicator) command as a JSON line.
    /// </summary>
    /// <param name="chatId">Target chat JID.</param>
    /// <returns>JSON string ready to write to stdin.</returns>
    public static string FormatComposingCommand(string chatId)
    {
        return JsonSerializer.Serialize(new { type = "composing", chatId }, WriteOptions);
    }

    /// <summary>
    /// Formats a ping command as a JSON line.
    /// </summary>
    /// <returns>JSON string ready to write to stdin.</returns>
    public static string FormatPingCommand()
    {
        return JsonSerializer.Serialize(new { type = "ping" }, WriteOptions);
    }

    /// <summary>
    /// Formats a shutdown command as a JSON line.
    /// </summary>
    /// <returns>JSON string ready to write to stdin.</returns>
    public static string FormatShutdownCommand()
    {
        return JsonSerializer.Serialize(new { type = "shutdown" }, WriteOptions);
    }

    /// <summary>
    /// Starts the Node.js Baileys bridge child process.
    /// Spawns background tasks for reading stdout/stderr and writing stdin.
    /// </summary>
    /// <param name="authDir">Directory for WhatsApp authentication state.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(string authDir, CancellationToken ct)
    {
        // Resolve script path relative to application base directory
        var resolvedScript = Path.Combine(AppContext.BaseDirectory, "node", "baileys-bridge.js");

        _logger.LogInformation("Starting WhatsApp bridge: node {Script} --auth-dir {AuthDir}", resolvedScript, authDir);

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = Path.GetDirectoryName(resolvedScript) ?? AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // UTF-8 *without* BOM — Encoding.UTF8 (the static) emits a BOM on the first
            // write, and the bridge's JSON.parse chokes on the leading U+FEFF.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(resolvedScript);
        psi.ArgumentList.Add("--auth-dir");
        psi.ArgumentList.Add(authDir);

        _process = new Process { StartInfo = psi };
        _process.Start();
        _logger.LogInformation("WhatsApp bridge started, pid={Pid}", _process.Id);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        // Stdin serialization via channel — single consumer drains to process stdin
        _stdinChannel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        _stdinTask = Task.Run(async () => await DrainStdinAsync(token), token);

        // Background task reading stdout line by line
        _stdoutTask = Task.Run(async () => await ReadStdoutAsync(token), token);

        // Background task reading stderr, forwarding to logger
        _stderrTask = Task.Run(async () => await ReadStderrAsync(token), token);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Writes a JSON line to the Node.js process stdin via the internal channel.
    /// </summary>
    /// <param name="jsonLine">JSON string to write.</param>
    public async Task WriteAsync(string jsonLine)
    {
        if (_stdinChannel is not null)
        {
            _stdinChannel.Writer.TryWrite(jsonLine);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends a text message and awaits the bridge's <c>sent</c> response, returning the
    /// resulting Baileys stanza ID (or null on send error/timeout). Each call uses a unique
    /// correlation ID so concurrent and out-of-order responses are correctly matched —
    /// even after a prior call has timed out.
    /// </summary>
    /// <param name="chatId">Target chat JID.</param>
    /// <param name="text">Message text to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stanza ID assigned by Baileys, or null if the send failed or timed out.</returns>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="ct"/> is cancelled.</exception>
    public async Task<string?> SendTextAndWaitAsync(string chatId, string text, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSends[correlationId] = tcs;

        try
        {
            await WriteAsync(FormatSendCommand(chatId, text, correlationId));

            using var timeoutCts = new CancellationTokenSource(SendTimeout);
            using var timeoutReg = timeoutCts.Token.Register(() => tcs.TrySetResult(null));
            using var ctReg = ct.Register(() => tcs.TrySetCanceled(ct));

            return await tcs.Task;
        }
        finally
        {
            _pendingSends.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Gracefully stops the Node.js process. Sends shutdown command, waits up to 5 seconds,
    /// then force-kills if the process has not exited.
    /// </summary>
    public async Task StopAsync()
    {
        if (_process is null)
            return;

        _logger.LogInformation("Stopping WhatsApp bridge");

        // Send shutdown command
        try
        {
            await WriteAsync(FormatShutdownCommand());
        }
        catch
        {
            // Process may already be dead
        }

        // Complete the stdin channel writer so the drain task finishes
        _stdinChannel?.Writer.TryComplete();

        // Wait for process to exit gracefully
        if (!_process.HasExited)
        {
            var exited = _process.WaitForExit(TimeSpan.FromSeconds(5));
            if (!exited)
            {
                _logger.LogWarning("WhatsApp bridge did not exit gracefully, killing process");
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Process may have already exited
                }
            }
        }

        // Cancel background tasks
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        // Wait for background tasks to finish
        var tasks = new List<Task>();
        if (_stdoutTask is not null) tasks.Add(_stdoutTask);
        if (_stderrTask is not null) tasks.Add(_stderrTask);
        if (_stdinTask is not null) tasks.Add(_stdinTask);

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Tasks may have been cancelled
        }

        _process.Dispose();
        _process = null;

        _logger.LogInformation("WhatsApp bridge stopped");
    }

    /// <summary>
    /// Disposes the process and cancels background tasks.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            await StopAsync();
        }

        _cts?.Dispose();
    }

    /// <summary>
    /// Reads stdout from the child process line by line, parses JSON, and invokes OnEvent.
    /// </summary>
    private async Task ReadStdoutAsync(CancellationToken ct)
    {
        if (_process is null) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct);
                if (line is null) break; // EOF — process exited

                var evt = ParseLine(line);
                if (evt is not null)
                {
                    // Intercept 'sent' events to resolve the pending SendTextAndWaitAsync.
                    // 'sent' events are an internal protocol detail — don't propagate to OnEvent.
                    if (evt.Type == "sent")
                    {
                        if (evt.CorrelationId is null)
                        {
                            _logger.LogWarning("WhatsApp bridge emitted 'sent' with no correlationId (id={Id}, message={Message})",
                                evt.Id, evt.Message);
                        }
                        else if (_pendingSends.TryRemove(evt.CorrelationId, out var pending))
                        {
                            pending.TrySetResult(evt.Id);
                            if (evt.Message is not null)
                                _logger.LogWarning("WhatsApp send {CorrelationId} returned error: {Message}",
                                    evt.CorrelationId, evt.Message);
                        }
                        else
                        {
                            _logger.LogWarning("WhatsApp bridge emitted 'sent' for unknown correlationId={CorrelationId} (id={Id})",
                                evt.CorrelationId, evt.Id);
                        }
                        continue;
                    }

                    try
                    {
                        OnEvent?.Invoke(evt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in OnEvent handler for event type={Type}", evt.Type);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading stdout from WhatsApp bridge");
        }
    }

    /// <summary>
    /// Reads stderr from the child process and forwards lines to the logger.
    /// </summary>
    private async Task ReadStderrAsync(CancellationToken ct)
    {
        if (_process is null) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(ct);
                if (line is null) break; // EOF

                _logger.LogWarning("[WhatsApp bridge stderr] {Line}", line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading stderr from WhatsApp bridge");
        }
    }

    /// <summary>
    /// Drains the stdin channel, writing each line to the child process stdin.
    /// Single consumer ensures serialized writes.
    /// </summary>
    private async Task DrainStdinAsync(CancellationToken ct)
    {
        if (_process is null || _stdinChannel is null) return;

        try
        {
            await foreach (var line in _stdinChannel.Reader.ReadAllAsync(ct))
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
                await _process.StandardInput.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to WhatsApp bridge stdin");
        }
    }
}
