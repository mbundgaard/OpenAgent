using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;
using OpenAgent.LlmVoice.OpenAIAzure.Protocol;
using OpenAgent.LlmVoice.OpenAIAzure.Models;

namespace OpenAgent.LlmVoice.OpenAIAzure;

/// <summary>
/// A live voice session backed by an Azure OpenAI Realtime WebSocket connection.
/// Streams audio in/out, handles server VAD, transcripts, and tool-call execution.
/// </summary>
internal sealed class AzureOpenAiVoiceSession : IVoiceSession
{
    private readonly AzureRealtimeConfig _config;
    private readonly Conversation _conversation;
    private readonly IAgentLogic _agentLogic;
    private readonly ILogger _logger;
    private readonly ClientWebSocket _ws = new();
    private readonly Channel<VoiceEvent> _channel = Channel.CreateUnbounded<VoiceEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCts = new();
    private Task _receiveTask = Task.CompletedTask;

    public string SessionId { get; private set; } = string.Empty;

    internal AzureOpenAiVoiceSession(AzureRealtimeConfig config, Conversation conversation, IAgentLogic agentLogic, ILogger logger)
    {
        _config = config;
        _conversation = conversation;
        _agentLogic = agentLogic;
        _logger = logger;
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        var host = new Uri(_config.Endpoint).Host;
        var uri = new Uri(
            $"wss://{host}/openai/realtime" +
            $"?api-version={_config.ApiVersion}&deployment={_conversation.Model}");

        _ws.Options.SetRequestHeader("api-key", _config.ApiKey);

        _logger.LogDebug("Connecting to Azure OpenAI Realtime at {Uri}", uri);
        await _ws.ConnectAsync(uri, ct);
        _logger.LogDebug("WebSocket connected for conversation {ConversationId}", _conversation.Id);

        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

        await SendSessionUpdateAsync(ct);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
    {
        var evt = new ClientEvent
        {
            Type = EventTypes.InputAudioBufferAppend,
            Audio = Convert.ToBase64String(audio.Span)
        };
        await SendEventAsync(evt, ct);
    }

    public async Task CommitAudioAsync(CancellationToken ct = default)
    {
        await SendEventAsync(new ClientEvent { Type = EventTypes.InputAudioBufferCommit }, ct);
    }

    public async Task CancelResponseAsync(CancellationToken ct = default)
    {
        await SendEventAsync(new ClientEvent { Type = EventTypes.ResponseCancel }, ct);
    }

    public async IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
        {
            yield return evt;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _receiveCts.CancelAsync();

        try { await _receiveTask; }
        catch (OperationCanceledException) { }

        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None);
            }
            catch { /* best-effort close */ }
        }

        _ws.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
        _channel.Writer.TryComplete();
    }

    private async Task SendSessionUpdateAsync(CancellationToken ct)
    {
        var tools = _agentLogic.Tools.Count > 0
            ? _agentLogic.Tools.Select(t => new RealtimeToolDefinition
            {
                Name = t.Name,
                Description = t.Description ?? "",
                Parameters = t.Parameters
            }).ToList()
            : null;

        var sessionConfig = new RealtimeSessionConfig
        {
            Modalities = ["audio", "text"],
            Voice = _config.Voice ?? "alloy",
            Instructions = _agentLogic.GetSystemPrompt(_conversation.Source, _conversation.Type, _conversation.ActiveSkills),
            InputAudioFormat = "pcm16",
            OutputAudioFormat = "pcm16",
            InputAudioTranscription = new InputAudioTranscriptionConfig { Model = "whisper-1" },
            TurnDetection = new TurnDetectionConfig { Type = "server_vad" },
            Tools = tools,
            ToolChoice = tools is not null ? "auto" : null
        };

        await SendEventAsync(new ClientEvent
        {
            Type = EventTypes.SessionUpdate,
            Session = sessionConfig
        }, ct);
    }

    private async Task SendToolResultAndContinueAsync(string callId, string result, CancellationToken ct)
    {
        await SendEventAsync(new ClientEvent
        {
            Type = EventTypes.ConversationItemCreate,
            Item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = result
            }
        }, ct);

        await SendEventAsync(new ClientEvent { Type = EventTypes.ResponseCreate }, ct);
    }

    private async Task SendEventAsync(ClientEvent evt, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(evt);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
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

                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    buffer.Write(rentedBuffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                var envelope = JsonSerializer.Deserialize<RealtimeEnvelope>(
                    buffer.WrittenSpan);

                if (envelope is null) continue;

                await HandleEnvelopeAsync(envelope, ct);
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket connection lost for conversation {ConversationId}", _conversation.Id);
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private async Task HandleEnvelopeAsync(RealtimeEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Type == EventTypes.FunctionCallArgumentsDone)
        {
            var name = envelope.Name ?? "";
            var arguments = envelope.Arguments ?? "";
            var callId = envelope.CallId ?? "";
            var conversationId = _conversation.Id;

            // Persist the tool call as an assistant message
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

            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug("Executing voice tool {ToolName} for conversation {ConversationId}", name, conversationId);
                    var result = await _agentLogic.ExecuteToolAsync(conversationId, name, arguments, ct);

                    // Persist tool result summary
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

                    // Persist error summary
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
            }, ct);
            return;
        }

        var voiceEvent = MapEvent(envelope);
        if (voiceEvent is not null)
        {
            // Persist completed transcripts as messages
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

    private VoiceEvent? MapEvent(RealtimeEnvelope envelope) => envelope.Type switch
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

    private VoiceEvent? HandleSessionCreated(RealtimeEnvelope envelope)
    {
        if (envelope.Session is { } session &&
            session.TryGetProperty("id", out var idProp))
        {
            SessionId = idProp.GetString() ?? "";
            _logger.LogDebug("Session created with ID {SessionId} for conversation {ConversationId}",
                SessionId, _conversation.Id);

            // Update conversation with session state
            _conversation.VoiceSessionId = SessionId;
            _conversation.VoiceSessionOpen = true;
            _agentLogic.UpdateConversation(_conversation);
        }
        return null;
    }

    private static string ExtractErrorMessage(RealtimeEnvelope envelope)
    {
        if (envelope.Error is { } error &&
            error.TryGetProperty("message", out var msgProp))
        {
            return msgProp.GetString() ?? "Unknown error";
        }
        return "Unknown error";
    }
}
