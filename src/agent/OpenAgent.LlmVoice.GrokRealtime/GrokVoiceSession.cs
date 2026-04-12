using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;
using OpenAgent.LlmVoice.GrokRealtime.Models;
using OpenAgent.LlmVoice.GrokRealtime.Protocol;

namespace OpenAgent.LlmVoice.GrokRealtime;

/// <summary>
/// A live voice session backed by the Grok Realtime WebSocket API.
/// Protocol-compatible with OpenAI Realtime; differs only in endpoint URL, auth header,
/// and two audio event type names.
/// </summary>
internal sealed class GrokVoiceSession : IVoiceSession
{
    private readonly GrokConfig _config;
    private readonly Conversation _conversation;
    private readonly IAgentLogic _agentLogic;
    private readonly ILogger _logger;
    private readonly ClientWebSocket _ws = new();
    private readonly Channel<VoiceEvent> _channel = Channel.CreateUnbounded<VoiceEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly ConcurrentDictionary<Task, byte> _toolTasks = new();
    private Task _receiveTask = Task.CompletedTask;

    public string SessionId { get; private set; } = string.Empty;

    internal GrokVoiceSession(GrokConfig config, Conversation conversation, IAgentLogic agentLogic, ILogger logger)
    {
        _config = config;
        _conversation = conversation;
        _agentLogic = agentLogic;
        _logger = logger;
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        var uri = new Uri("wss://api.x.ai/v1/realtime");

        // Grok uses Authorization: Bearer, not api-key
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");

        _logger.LogDebug("Connecting to Grok Realtime for conversation {ConversationId}", _conversation.Id);
        await _ws.ConnectAsync(uri, ct);

        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

        await SendSessionUpdateAsync(ct);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
    {
        await SendEventAsync(new GrokClientEvent
        {
            Type = EventTypes.InputAudioBufferAppend,
            Audio = Convert.ToBase64String(audio.Span)
        }, ct);
    }

    public async Task CommitAudioAsync(CancellationToken ct = default)
    {
        await SendEventAsync(new GrokClientEvent { Type = EventTypes.InputAudioBufferCommit }, ct);
    }

    public async Task CancelResponseAsync(CancellationToken ct = default)
    {
        await SendEventAsync(new GrokClientEvent { Type = EventTypes.ResponseCancel }, ct);
    }

    public async IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    public async ValueTask DisposeAsync()
    {
        await _receiveCts.CancelAsync();

        try { await _receiveTask; }
        catch (OperationCanceledException) { }

        // Wait for in-flight tool tasks to finish so they don't hit disposed primitives.
        var pending = _toolTasks.Keys.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending); }
            catch { /* individual failures already logged */ }
        }

        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None); }
            catch { /* best-effort */ }
        }

        if (_conversation.VoiceSessionOpen)
        {
            _conversation.VoiceSessionOpen = false;
            try { _agentLogic.UpdateConversation(_conversation); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear VoiceSessionOpen for {ConversationId}", _conversation.Id); }
        }

        _ws.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
        _channel.Writer.TryComplete();
    }

    private async Task SendSessionUpdateAsync(CancellationToken ct)
    {
        var tools = _agentLogic.Tools.Count > 0
            ? _agentLogic.Tools.Select(t => new GrokToolDefinition
            {
                Name = t.Name,
                Description = t.Description ?? "",
                Parameters = t.Parameters
            }).ToList()
            : null;

        var codec = NormalizeCodec(_config.Codec);
        var rate = ResolveRate(_config.SampleRate, codec);

        await SendEventAsync(new GrokClientEvent
        {
            Type = EventTypes.SessionUpdate,
            Session = new GrokSessionConfig
            {
                Modalities = ["audio", "text"],
                Voice = _config.Voice ?? "rex",
                Instructions = _agentLogic.GetSystemPrompt(_conversation.Source, _conversation.Type, _conversation.ActiveSkills),
                Audio = new GrokAudioConfig
                {
                    Input = new GrokAudioDirection { Format = new GrokAudioFormat { Type = CodecToWire(codec), Rate = rate } },
                    Output = new GrokAudioDirection { Format = new GrokAudioFormat { Type = CodecToWire(codec), Rate = rate } }
                },
                TurnDetection = new GrokTurnDetectionConfig { Type = "server_vad" },
                Tools = tools,
                ToolChoice = tools is not null ? "auto" : null
            }
        }, ct);

        // Advertise the negotiated audio format so the client can configure capture/playback.
        await _channel.Writer.WriteAsync(new SessionReady(
            InputSampleRate: rate,
            OutputSampleRate: rate,
            InputCodec: codec,
            OutputCodec: codec), ct);
    }

    // "pcm16" | "g711_ulaw" | "g711_alaw"
    private static string NormalizeCodec(string? codec) => codec switch
    {
        "g711_ulaw" => "g711_ulaw",
        "g711_alaw" => "g711_alaw",
        _ => "pcm16"
    };

    // Neutral codec → xAI wire value
    private static string CodecToWire(string codec) => codec switch
    {
        "g711_ulaw" => "audio/pcmu",
        "g711_alaw" => "audio/pcma",
        _ => "audio/pcm"
    };

    private static int ResolveRate(string? configured, string codec)
    {
        // μ-law / A-law are fixed at 8 kHz regardless of configured rate.
        if (codec is "g711_ulaw" or "g711_alaw") return 8000;
        return int.TryParse(configured, out var rate) ? rate : 24000;
    }

    private async Task SendToolResultAndContinueAsync(string callId, string result, CancellationToken ct)
    {
        await SendEventAsync(new GrokClientEvent
        {
            Type = EventTypes.ConversationItemCreate,
            Item = new { type = "function_call_output", call_id = callId, output = result }
        }, ct);

        await SendEventAsync(new GrokClientEvent { Type = EventTypes.ResponseCreate }, ct);
    }

    private async Task SendEventAsync(GrokClientEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(evt);
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(json, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var rentedBuffer = new byte[65536];

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                buffer.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(rentedBuffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    buffer.Write(rentedBuffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                var envelope = JsonSerializer.Deserialize<GrokEnvelope>(buffer.WrittenSpan);
                if (envelope is null) continue;

                await HandleEnvelopeAsync(envelope, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Grok WebSocket connection lost for conversation {ConversationId}", _conversation.Id);
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private async Task HandleEnvelopeAsync(GrokEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Type == EventTypes.FunctionCallArgumentsDone)
        {
            var name = envelope.Name ?? "";
            var arguments = envelope.Arguments ?? "";
            var callId = envelope.CallId ?? "";
            var conversationId = _conversation.Id;

            var toolCalls = JsonSerializer.Serialize(new[]
            {
                new { id = callId, type = "function", function = new { name, arguments } }
            });
            _agentLogic.AddMessage(conversationId, new Message
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Role = "assistant",
                ToolCalls = toolCalls,
                Modality = MessageModality.Voice
            });

            Task toolTask = null!;
            toolTask = Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug("Executing voice tool {ToolName} for conversation {ConversationId}", name, conversationId);
                    var result = await _agentLogic.ExecuteToolAsync(conversationId, name, arguments, ct);

                    _agentLogic.AddMessage(conversationId, new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = conversationId,
                        Role = "tool",
                        Content = ToolResultSummary.Create(name, result),
                        ToolCallId = callId,
                        Modality = MessageModality.Voice
                    });

                    await SendToolResultAndContinueAsync(callId, result, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Voice tool {ToolName} failed for conversation {ConversationId}", name, conversationId);
                    var errorResult = JsonSerializer.Serialize(new { error = ex.Message });

                    _agentLogic.AddMessage(conversationId, new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = conversationId,
                        Role = "tool",
                        Content = ToolResultSummary.Create(name, errorResult),
                        ToolCallId = callId,
                        Modality = MessageModality.Voice
                    });

                    await SendToolResultAndContinueAsync(callId, errorResult, ct);
                }
                finally
                {
                    _toolTasks.TryRemove(toolTask, out _);
                }
            }, ct);
            _toolTasks.TryAdd(toolTask, 0);
            return;
        }

        var voiceEvent = MapEvent(envelope);
        if (voiceEvent is not null)
        {
            if (voiceEvent is TranscriptDone td)
            {
                var role = td.Source == TranscriptSource.User ? "user" : "assistant";
                _agentLogic.AddMessage(_conversation.Id, new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    ConversationId = _conversation.Id,
                    Role = role,
                    Content = td.Text,
                    Modality = MessageModality.Voice
                });
            }

            await _channel.Writer.WriteAsync(voiceEvent, ct);
        }
    }

    private VoiceEvent? MapEvent(GrokEnvelope envelope) => envelope.Type switch
    {
        EventTypes.SessionCreated => HandleSessionCreated(envelope),
        EventTypes.SpeechStarted => new SpeechStarted(),
        EventTypes.SpeechStopped => new SpeechStopped(),
        EventTypes.ResponseAudioDelta => new AudioDelta(Convert.FromBase64String(envelope.Delta ?? "")),
        EventTypes.ResponseAudioDone => new AudioDone(),
        EventTypes.InputAudioTranscriptionDelta => new TranscriptDelta(envelope.Delta ?? "", TranscriptSource.User),
        EventTypes.InputAudioTranscriptionCompleted => new TranscriptDone(envelope.Transcript ?? "", TranscriptSource.User),
        EventTypes.ResponseAudioTranscriptDelta => new TranscriptDelta(envelope.Delta ?? "", TranscriptSource.Assistant),
        EventTypes.ResponseAudioTranscriptDone => new TranscriptDone(envelope.Transcript ?? "", TranscriptSource.Assistant),
        EventTypes.Error => new SessionError(ExtractErrorMessage(envelope)),
        _ => null
    };

    private VoiceEvent? HandleSessionCreated(GrokEnvelope envelope)
    {
        if (envelope.Session is { } session &&
            session.TryGetProperty("id", out var idProp))
        {
            SessionId = idProp.GetString() ?? "";
            _logger.LogDebug("Grok session created {SessionId} for conversation {ConversationId}",
                SessionId, _conversation.Id);
            _conversation.VoiceSessionId = SessionId;
            _conversation.VoiceSessionOpen = true;
            _agentLogic.UpdateConversation(_conversation);
        }
        return null;
    }

    private static string ExtractErrorMessage(GrokEnvelope envelope)
    {
        if (envelope.Error is { } error && error.TryGetProperty("message", out var msgProp))
            return msgProp.GetString() ?? "Unknown error";
        return "Unknown error";
    }
}
