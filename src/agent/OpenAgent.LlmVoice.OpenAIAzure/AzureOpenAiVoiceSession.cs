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
    // Mutable so RebindConversationAsync can swap the bound conversation in flight (DTMF
    // extension routing on Telnyx). Subsequent persists in HandleEnvelopeAsync read this
    // field directly so they land on the new conversation post-swap; in-flight tasks that
    // already captured the old id may still write to it — accepted per the design.
    private Conversation _conversation;
    private readonly IAgentLogic _agentLogic;
    private readonly ILogger _logger;
    private readonly string _codec;
    private readonly int _sampleRate;
    private readonly ClientWebSocket _ws = new();
    private readonly Channel<VoiceEvent> _channel = Channel.CreateUnbounded<VoiceEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCts = new();
    private Task _receiveTask = Task.CompletedTask;
    // Watchdog: tracks the most recent ResponseAudioDelta wallclock so we can detect when the
    // realtime API goes silent after we send a tool result. Compared against the timestamp at
    // which we sent the tool result + response.create — if no audio arrives within 15s, we warn.
    private DateTimeOffset _audioLastReceivedAt;

    public string SessionId { get; private set; } = string.Empty;

    internal AzureOpenAiVoiceSession(
        AzureRealtimeConfig config,
        Conversation conversation,
        IAgentLogic agentLogic,
        VoiceSessionOptions? options,
        ILogger logger)
    {
        _config = config;
        _conversation = conversation;
        _agentLogic = agentLogic;
        _logger = logger;

        var requested = options ?? new VoiceSessionOptions("pcm16", 24000);
        var expectedRate = RateForCodec(requested.Codec);
        if (requested.SampleRate != expectedRate)
            throw new ArgumentException(
                $"Azure Realtime supports {requested.Codec} only at {expectedRate} Hz, got {requested.SampleRate}.",
                nameof(options));
        _codec = requested.Codec;
        _sampleRate = requested.SampleRate;
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

    /// <summary>
    /// Re-sends the session.update event with a freshly built system prompt. Called after the
    /// agent mutates conversation state that affects the prompt (e.g. activate_skill) so the
    /// model sees the change without requiring a new call.
    /// </summary>
    public async Task RefreshSystemPromptAsync(CancellationToken ct = default)
    {
        // Send only the session.update — skip the SessionReady channel write, which is a
        // one-time client signal at session bring-up, not something we re-broadcast on refresh.
        await SendEventAsync(new ClientEvent
        {
            Type = EventTypes.SessionUpdate,
            Session = BuildSessionConfig()
        }, ct);
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
        await SendEventAsync(new ClientEvent
        {
            Type = EventTypes.SessionUpdate,
            Session = BuildSessionConfig()
        }, ct);

        // Advertise negotiated audio format to the client. OpenAI Realtime has fixed rates per codec.
        await _channel.Writer.WriteAsync(new SessionReady(
            InputSampleRate: _sampleRate,
            OutputSampleRate: _sampleRate,
            InputCodec: _codec,
            OutputCodec: _codec), ct);
    }

    private RealtimeSessionConfig BuildSessionConfig()
    {
        var tools = _agentLogic.Tools.Count > 0
            ? _agentLogic.Tools.Select(t => new RealtimeToolDefinition
            {
                Name = t.Name,
                Description = t.Description ?? "",
                Parameters = t.Parameters
            }).ToList()
            : null;

        // Re-fetch the conversation from the store so mid-session mutations (activate_skill,
        // set_intention, etc.) are reflected on refresh. The captured _conversation reference
        // is a snapshot from session start — stale by the time tools mutate state via the store.
        var live = _agentLogic.GetConversation(_conversation.Id) ?? _conversation;
        var instructions = _agentLogic.GetSystemPrompt(live.Id, live.Source, voice: true, live.ActiveSkills, live.Intention);
        // Realtime sessions don't replay conversation history on connect, so the compaction
        // summary (which holds long-term context for older turns) goes into the system prompt
        // rather than as a separate user item. Live post-cut messages are seeded separately
        // by the caller via AddUserMessageAsync.
        if (!string.IsNullOrWhiteSpace(live.Context))
            instructions += "\n\n<summary>\n" + live.Context.Trim() + "\n</summary>";

        return new RealtimeSessionConfig
        {
            Modalities = ["audio", "text"],
            Voice = _config.Voice ?? "alloy",
            Instructions = instructions,
            InputAudioFormat = _codec,
            OutputAudioFormat = _codec,
            InputAudioTranscription = new InputAudioTranscriptionConfig { Model = "whisper-1" },
            TurnDetection = new TurnDetectionConfig { Type = "server_vad" },
            Tools = tools,
            ToolChoice = tools is not null ? "auto" : null
        };
    }

    public async Task AddUserMessageAsync(string text, CancellationToken ct = default)
    {
        // Adds a user-role message to the realtime conversation buffer. No response trigger —
        // callers that want a reply must call RequestResponseAsync after.
        await SendEventAsync(new ClientEvent
        {
            Type = EventTypes.ConversationItemCreate,
            Item = new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text } }
            }
        }, ct);
    }

    public async Task RequestResponseAsync(CancellationToken ct = default)
    {
        await SendEventAsync(new ClientEvent { Type = EventTypes.ResponseCreate }, ct);
    }

    public async Task RebindConversationAsync(Conversation newConversation, CancellationToken ct = default)
    {
        // Swap the bound conversation FIRST so BuildSessionConfig (called by RefreshSystemPrompt)
        // and any subsequent persistence land on the new conversation.
        var oldId = _conversation.Id;
        _conversation = newConversation;

        // Push the new system prompt (skills, intention, summary all derived from the new conv).
        await RefreshSystemPromptAsync(ct);

        // Inject the new conversation's prior messages so the model has its history. Skip tool
        // role and orphan tool-call assistant rows — the model only needs human-readable turns.
        var stored = _agentLogic.GetMessages(newConversation.Id, includeToolResultBlobs: false);
        var injected = 0;
        foreach (var message in stored)
        {
            if (message.Role is "tool") continue;
            if (string.IsNullOrEmpty(message.Content)) continue;
            if (message.ToolCalls is not null) continue;
            await SendConversationItemAsync(message.Role, message.Content, ct);
            injected++;
        }

        _logger.LogInformation(
            "Rebound voice session: {OldConversation} -> {NewConversation} ({Injected} messages injected)",
            oldId, newConversation.Id, injected);
    }

    private async Task SendConversationItemAsync(string role, string text, CancellationToken ct)
    {
        // Assistant-role items use content-type "text"; user-role items use "input_text".
        var item = role == "assistant"
            ? (object)new
            {
                type = "message",
                role = "assistant",
                content = new[] { new { type = "text", text } }
            }
            : new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text } }
            };

        await SendEventAsync(new ClientEvent
        {
            Type = EventTypes.ConversationItemCreate,
            Item = item
        }, ct);
    }

    private static int RateForCodec(string codec) => codec switch
    {
        "g711_ulaw" or "g711_alaw" => 8000,
        _ => 24000
    };

    private async Task SendToolResultAndContinueAsync(string toolName, string callId, string result, CancellationToken ct)
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

        // Stall watchdog: capture the moment we asked for a response, then check 15 seconds later
        // whether any audio came back. If _audioLastReceivedAt is still earlier than sentAt, the
        // realtime API has gone silent on us — log a warning so we can see it happening live.
        // Fire-and-forget so the receive loop isn't blocked.
        var sentAt = DateTimeOffset.UtcNow;
        var conversationId = _conversation.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                if (_audioLastReceivedAt < sentAt)
                {
                    _logger.LogWarning(
                        "Realtime stalled: no audio response 15s after sending tool result for {ToolName} (call {CallId}, conversation {ConversationId})",
                        toolName, callId, conversationId);
                }
            }
            catch (OperationCanceledException) { /* session torn down before watchdog fired */ }
        }, ct);
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
        // One log line per realtime envelope, regardless of whether we route it. Lets us see
        // exactly what Azure OpenAI is (and isn't) sending when something goes wrong mid-call.
        _logger.LogDebug("Realtime event {EventType} for conversation {ConversationId}",
            envelope.Type, _conversation.Id);

        // Watchdog timestamp: any audio delta from the model resets the clock so the stall
        // detector after tool results can tell "no audio came back" from "audio is flowing".
        if (envelope.Type == EventTypes.ResponseAudioDelta)
            _audioLastReceivedAt = DateTimeOffset.UtcNow;

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
                // Emit thinking-cue before the tool runs so the endpoint can push a placeholder.
                await _channel.Writer.WriteAsync(new VoiceToolCallStarted(name, callId), ct);
                string? completionResult = null;
                try
                {
                    _logger.LogDebug("Executing voice tool {ToolName} for conversation {ConversationId}", name, conversationId);
                    var result = await _agentLogic.ExecuteToolAsync(conversationId, name, arguments, ct);
                    completionResult = result;

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

                    await SendToolResultAndContinueAsync(name, callId, result, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Voice tool {ToolName} failed for conversation {ConversationId}", name, conversationId);
                    var errorResult = JsonSerializer.Serialize(new { error = ex.Message });
                    completionResult = errorResult;

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

                    await SendToolResultAndContinueAsync(name, callId, errorResult, ct);
                }
                finally
                {
                    // Always emit the completed cue — even on cancellation — so the browser/Telnyx pump
                    // never gets stuck on thinking_started. TryWrite is safe during channel completion.
                    _channel.Writer.TryWrite(new VoiceToolCallCompleted(callId, completionResult ?? "cancelled"));
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
