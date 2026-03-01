using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using OpenAgent.Contracts;
using OpenAgent.Models.Voice;
using OpenAgent.LlmVoice.OpenAIAzure.Protocol;
using OpenAgent.LlmVoice.OpenAIAzure.Models;

namespace OpenAgent.LlmVoice.OpenAIAzure;

internal sealed class AzureOpenAiVoiceSession : IVoiceSession
{
    private readonly AzureRealtimeConfig _config;
    private readonly VoiceSessionOptions _options;
    private readonly IAgentLogic _agentLogic;
    private readonly ClientWebSocket _ws = new();
    private readonly Channel<VoiceEvent> _channel = Channel.CreateUnbounded<VoiceEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCts = new();
    private Task _receiveTask = Task.CompletedTask;

    public string SessionId { get; private set; } = string.Empty;

    internal AzureOpenAiVoiceSession(AzureRealtimeConfig config, VoiceSessionOptions options, IAgentLogic agentLogic)
    {
        _config = config;
        _options = options;
        _agentLogic = agentLogic;
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        var uri = new Uri(
            $"wss://{_config.ResourceName}.openai.azure.com/openai/realtime" +
            $"?api-version={_config.ApiVersion}&deployment={_config.DeploymentName}");

        _ws.Options.SetRequestHeader("api-key", _config.ApiKey);

        await _ws.ConnectAsync(uri, ct);

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
            ? _agentLogic.Tools.Select(t => new
            {
                type = "function",
                name = t.Name,
                description = t.Description ?? "",
                parameters = t.Parameters
            }).ToArray()
            : null;

        var sessionConfig = new
        {
            modalities = new[] { "audio", "text" },
            voice = _options.Voice ?? "alloy",
            instructions = _agentLogic.SystemPrompt,
            input_audio_format = "pcm16",
            output_audio_format = "pcm16",
            input_audio_transcription = new { model = "whisper-1" },
            turn_detection = new { type = "server_vad" },
            tools,
            tool_choice = tools is not null ? "auto" : (string?)null
        };

        var evt = new ClientEvent
        {
            Type = EventTypes.SessionUpdate,
            Session = sessionConfig
        };

        await SendEventAsync(evt, ct);
    }

    private async Task SendToolResultAsync(string callId, string result, CancellationToken ct)
    {
        var evt = new ClientEvent
        {
            Type = EventTypes.ConversationItemCreate,
            Item = new
            {
                type = "function_call_output",
                call_id = callId,
                output = result
            }
        };
        await SendEventAsync(evt, ct);
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
        var rentedBuffer = new byte[8192];

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
        catch (WebSocketException) { /* connection lost */ }
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

            var toolResult = await _agentLogic.ExecuteToolAsync(name, arguments, ct);
            await SendToolResultAsync(callId, toolResult, ct);
            return;
        }

        var voiceEvent = MapEvent(envelope);
        if (voiceEvent is not null)
        {
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
