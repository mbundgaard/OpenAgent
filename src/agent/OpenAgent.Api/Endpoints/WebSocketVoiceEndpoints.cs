using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;

namespace OpenAgent.Api.Endpoints;

/// <summary>
/// Bidirectional voice streaming over WebSocket — bridges client audio to a realtime voice LLM session.
/// Binary frames carry PCM audio; JSON text frames carry control events (transcripts, speech detection, errors).
/// </summary>
public static class WebSocketVoiceEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Maps /ws/conversations/{conversationId}/voice for bidirectional voice streaming.
    /// </summary>
    public static void MapWebSocketVoiceEndpoints(this WebApplication app)
    {
        app.Map("/ws/conversations/{conversationId}/voice", async (string conversationId, HttpContext context,
            IConversationStore store, IVoiceSessionManager sessionManager, AgentConfig agentConfig) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var conversation = store.GetOrCreate(conversationId, "app", ConversationType.Voice, agentConfig.VoiceProvider, agentConfig.VoiceModel);
            store.UpdateType(conversationId, ConversationType.Voice);
            conversation.Type = ConversationType.Voice;

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(WebSocketVoiceEndpoints));

            IVoiceSession session;
            try
            {
                session = await sessionManager.GetOrCreateSessionAsync(conversationId, context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create voice session for conversation {ConversationId}", conversationId);
                var errorJson = JsonSerializer.SerializeToUtf8Bytes(
                    new VoiceErrorEvent { Type = "error", Message = $"Session creation failed: {ex.Message}" }, JsonOptions);
                await ws.SendAsync(errorJson, WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, ex.Message.Length > 120 ? ex.Message[..120] : ex.Message, CancellationToken.None);
                return;
            }

            try
            {
                await RunBridgeAsync(ws, session, context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Voice bridge failed for conversation {ConversationId}", conversationId);
            }
            finally
            {
                await sessionManager.CloseSessionAsync(conversationId);

                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    catch { /* best-effort close */ }
                }
            }
        }).RequireAuthorization();
    }

    private static async Task RunBridgeAsync(WebSocket ws, IVoiceSession session, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readTask = ReadLoopAsync(ws, session, cts.Token);
        var writeTask = WriteLoopAsync(ws, session, cts.Token);

        await Task.WhenAny(readTask, writeTask);
        await cts.CancelAsync();

        try { await readTask; } catch (OperationCanceledException) { }
        try { await writeTask; } catch (OperationCanceledException) { }
    }

    private static async Task ReadLoopAsync(WebSocket ws, IVoiceSession session, CancellationToken ct)
    {
        var buffer = new byte[16384];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                await session.SendAudioAsync(buffer.AsMemory(0, result.Count), ct);
            }
        }
    }

    private static async Task WriteLoopAsync(WebSocket ws, IVoiceSession session, CancellationToken ct)
    {
        await foreach (var evt in session.ReceiveEventsAsync(ct))
        {
            if (ws.State != WebSocketState.Open)
                break;

            switch (evt)
            {
                case AudioDelta audio:
                    await ws.SendAsync(audio.Audio, WebSocketMessageType.Binary, true, ct);
                    break;

                case SpeechStarted:
                    await SendJsonAsync(ws, new VoiceWebSocketEvent { Type = "speech_started" }, ct);
                    break;

                case SpeechStopped:
                    await SendJsonAsync(ws, new VoiceWebSocketEvent { Type = "speech_stopped" }, ct);
                    break;

                case AudioDone:
                    await SendJsonAsync(ws, new VoiceWebSocketEvent { Type = "audio_done" }, ct);
                    break;

                case TranscriptDelta td:
                    await SendJsonAsync(ws, new VoiceTranscriptEvent
                    {
                        Type = "transcript_delta",
                        Text = td.Text,
                        Source = td.Source.ToString().ToLowerInvariant()
                    }, ct);
                    break;

                case TranscriptDone td:
                    await SendJsonAsync(ws, new VoiceTranscriptEvent
                    {
                        Type = "transcript_done",
                        Text = td.Text,
                        Source = td.Source.ToString().ToLowerInvariant()
                    }, ct);
                    break;

                case SessionError err:
                    await SendJsonAsync(ws, new VoiceErrorEvent
                    {
                        Type = "error",
                        Message = err.Message
                    }, ct);
                    break;
            }
        }
    }

    private static async Task SendJsonAsync<T>(WebSocket ws, T value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
    }
}
