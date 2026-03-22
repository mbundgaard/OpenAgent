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

    /// <summary>
    /// Maps /ws/terminal/{sessionId} for bidirectional terminal streaming.
    /// </summary>
    public static void MapWebSocketTerminalEndpoints(this WebApplication app)
    {
        app.Map("/ws/terminal/{sessionId}", async (string sessionId, HttpContext context,
            ITerminalSessionManager sessionManager, AgentEnvironment environment) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(WebSocketTerminalEndpoints));

            // Get or create terminal session
            ITerminalSession session;
            try
            {
                session = sessionManager.Get(sessionId)
                    ?? sessionManager.Create(sessionId, environment.DataPath);
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

            try
            {
                await RunBridgeAsync(ws, session, context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Terminal bridge failed for session {SessionId}", sessionId);
            }
            finally
            {
                // Don't close the session on WebSocket disconnect — allow reconnection.
                // Sessions are cleaned up via explicit close or idle timeout.

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
    private static async Task RunBridgeAsync(WebSocket ws, ITerminalSession session, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readTask = ReadLoopAsync(ws, session, cts.Token);
        var writeTask = WriteLoopAsync(ws, session, cts.Token);

        // When either loop ends, cancel the other
        await Task.WhenAny(readTask, writeTask);
        await cts.CancelAsync();

        try { await readTask; } catch (OperationCanceledException) { }
        try { await writeTask; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Reads from WebSocket and writes to PTY.
    /// Binary frames = keystrokes, text frames = JSON control messages (resize).
    /// </summary>
    private static async Task ReadLoopAsync(WebSocket ws, ITerminalSession session, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Raw keystrokes — forward to PTY
                session.Write(buffer.AsSpan(0, result.Count));
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                // JSON control message — currently only resize
                HandleControlMessage(buffer.AsSpan(0, result.Count), session);
            }
        }
    }

    /// <summary>
    /// Reads from PTY and writes to WebSocket as binary frames.
    /// </summary>
    private static async Task WriteLoopAsync(WebSocket ws, ITerminalSession session, CancellationToken ct)
    {
        await foreach (var chunk in session.ReadOutputAsync(ct))
        {
            if (ws.State != WebSocketState.Open)
                break;

            await ws.SendAsync(chunk, WebSocketMessageType.Binary, true, ct);
        }
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
