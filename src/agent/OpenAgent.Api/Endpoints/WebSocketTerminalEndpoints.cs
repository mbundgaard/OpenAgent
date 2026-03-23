using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Bidirectional terminal streaming over WebSocket — bridges xterm.js to a PTY session.
/// Binary frames carry raw terminal I/O; JSON text frames carry control events (resize).
/// </summary>
public static class WebSocketTerminalEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // Tracks the active bridge per session — new connections cancel the previous bridge
    // to prevent two consumers fighting over the same PTY output stream.
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> ActiveBridges = new();

    /// <summary>
    /// Maps /ws/terminal/{sessionId} for bidirectional terminal streaming.
    /// </summary>
    public static void MapWebSocketTerminalEndpoints(this WebApplication app)
    {
        app.Map("/ws/terminal/{sessionId}", async (string sessionId, HttpContext context,
            ITerminalSessionManager sessionManager, AgentEnvironment environment) =>
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(WebSocketTerminalEndpoints));

            logger.LogInformation("Terminal WS request for session {SessionId}, IsWebSocket={IsWs}",
                sessionId, context.WebSockets.IsWebSocketRequest);

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();

            // Cancel any previous bridge on this session — only one WS consumer per PTY
            if (ActiveBridges.TryRemove(sessionId, out var previousCts))
            {
                logger.LogInformation("Evicting previous bridge for terminal {SessionId}", sessionId);
                await previousCts.CancelAsync();
                previousCts.Dispose();
            }

            // Get or create terminal session
            ITerminalSession session;
            try
            {
                session = sessionManager.Get(sessionId)
                    ?? sessionManager.Create(sessionId, environment.DataPath);
                logger.LogInformation("Terminal session {SessionId} ready", sessionId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create terminal session {SessionId}", sessionId);
                var errorBytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { type = "error", message = ex.Message }, JsonOptions));
                await ws.SendAsync(errorBytes, WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.InternalServerError,
                    ex.Message.Length > 120 ? ex.Message[..120] : ex.Message, CancellationToken.None);
                return;
            }

            // Register this bridge so future connections can evict it
            var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            ActiveBridges[sessionId] = bridgeCts;

            try
            {
                logger.LogInformation("Starting bridge for terminal {SessionId}", sessionId);
                await RunBridgeAsync(ws, session, logger, bridgeCts.Token);
                logger.LogInformation("Bridge ended for terminal {SessionId}", sessionId);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Bridge cancelled for terminal {SessionId} (evicted or disconnected)", sessionId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Terminal bridge failed for session {SessionId}", sessionId);
            }
            finally
            {
                // Remove ourselves if we're still the active bridge
                ActiveBridges.TryRemove(sessionId, out _);
                bridgeCts.Dispose();

                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    catch { /* best-effort close */ }
                }
            }
        }).RequireAuthorization();
    }

    /// <summary>
    /// Dual read/write bridge between WebSocket and PTY session.
    /// Cancels both loops when either completes.
    /// </summary>
    private static async Task RunBridgeAsync(WebSocket ws, ITerminalSession session,
        ILogger logger, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readTask = ReadLoopAsync(ws, session, logger, cts.Token);
        var writeTask = WriteLoopAsync(ws, session, logger, cts.Token);

        // When either loop ends, cancel the other
        var completed = await Task.WhenAny(readTask, writeTask);
        logger.LogInformation("Bridge loop ended: {Loop}, cancelling other",
            completed == readTask ? "ReadLoop" : "WriteLoop");
        await cts.CancelAsync();

        try { await readTask; } catch (OperationCanceledException) { }
        try { await writeTask; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Reads from WebSocket and writes to PTY.
    /// Binary frames = keystrokes, text frames = JSON control messages (resize).
    /// </summary>
    private static async Task ReadLoopAsync(WebSocket ws, ITerminalSession session,
        ILogger logger, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            logger.LogDebug("ReadLoop waiting for WS frame, WsState={State}", ws.State);
            var result = await ws.ReceiveAsync(buffer, ct);
            logger.LogDebug("ReadLoop received: Type={Type}, Count={Count}", result.MessageType, result.Count);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                logger.LogInformation("ReadLoop: client sent Close frame");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Raw keystrokes — forward to PTY
                session.Write(buffer.AsSpan(0, result.Count));
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                // JSON control message — currently only resize
                logger.LogInformation("ReadLoop: control message received ({Count} bytes)", result.Count);
                HandleControlMessage(buffer.AsSpan(0, result.Count), session);
            }
        }

        logger.LogInformation("ReadLoop exiting, WsState={State}, Cancelled={Cancelled}",
            ws.State, ct.IsCancellationRequested);
    }

    /// <summary>
    /// Reads from PTY and writes to WebSocket as binary frames.
    /// </summary>
    private static async Task WriteLoopAsync(WebSocket ws, ITerminalSession session,
        ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("WriteLoop starting, reading PTY output");
        await foreach (var chunk in session.ReadOutputAsync(ct))
        {
            if (ws.State != WebSocketState.Open)
            {
                logger.LogInformation("WriteLoop: WS no longer open ({State}), stopping", ws.State);
                break;
            }

            logger.LogDebug("WriteLoop: sending {Length} bytes to WS", chunk.Length);
            await ws.SendAsync(chunk, WebSocketMessageType.Binary, true, ct);
        }

        logger.LogInformation("WriteLoop exiting, WsState={State}", ws.State);
    }

    /// <summary>
    /// Parses a JSON control message from a text frame and applies it (e.g. resize).
    /// </summary>
    private static void HandleControlMessage(ReadOnlySpan<byte> data, ITerminalSession session)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray());
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "resize")
            {
                var cols = root.GetProperty("cols").GetInt32();
                var rows = root.GetProperty("rows").GetInt32();
                session.Resize(cols, rows);
            }
        }
        catch
        {
            // Ignore malformed control messages
        }
    }
}
