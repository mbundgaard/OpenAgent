using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Voice;
using OpenAgent.LlmVoice.GeminiLive.Models;

namespace OpenAgent.LlmVoice.GeminiLive;

/// <summary>
/// A live voice session backed by the Google Gemini Live WebSocket API.
/// Uses a proprietary protocol (not OpenAI-compatible): key-presence event dispatch,
/// structured tool args, and a 15-minute session cap handled via proactive reconnect.
/// </summary>
internal sealed class GeminiLiveVoiceSession : IVoiceSession
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly GeminiConfig _config;
    private readonly Conversation _conversation;
    private readonly IAgentLogic _agentLogic;
    private readonly ILogger _logger;

    private readonly Channel<VoiceEvent> _channel = Channel.CreateUnbounded<VoiceEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCts = new();

    // Per-connection state — replaced on reconnect
    private ClientWebSocket _ws = new();
    private CancellationTokenSource _receiveCts = new();
    private Task _receiveTask = Task.CompletedTask;
    private TaskCompletionSource _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Reconnect guard — 0 = idle, 1 = reconnecting
    private int _reconnecting;
    private Timer? _reconnectTimer;

    // Transcript accumulators — flushed and persisted on turn complete
    private readonly StringBuilder _assistantTranscript = new();
    private readonly StringBuilder _userTranscript = new();

    public string SessionId { get; private set; } = string.Empty;

    internal GeminiLiveVoiceSession(GeminiConfig config, Conversation conversation, IAgentLogic agentLogic, ILogger logger)
    {
        _config = config;
        _conversation = conversation;
        _agentLogic = agentLogic;
        _logger = logger;
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        await EstablishConnectionAsync(ct);
        ArmReconnectTimer();
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
    {
        // Drop frames silently during reconnect — brief gap is acceptable
        if (Volatile.Read(ref _reconnecting) == 1) return;

        await SendMessageAsync(new GeminiClientMessage
        {
            RealtimeInput = new GeminiRealtimeInput
            {
                Audio = new GeminiRealtimeAudio
                {
                    RealtimeInputAudio = new GeminiRealtimeInputAudio
                    {
                        Data = Convert.ToBase64String(audio.Span)
                    }
                }
            }
        }, ct);
    }

    public Task CommitAudioAsync(CancellationToken ct = default)
    {
        // Gemini uses server-side VAD with continuous streaming — no explicit commit needed
        return Task.CompletedTask;
    }

    public async Task CancelResponseAsync(CancellationToken ct = default)
    {
        await SendMessageAsync(new GeminiClientMessage
        {
            ClientContent = new GeminiClientContent { Turns = [], TurnComplete = true }
        }, ct);
    }

    public async IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    /// <summary>
    /// Gemini Live's <c>setup</c> is a one-shot bring-up message — there's no equivalent of
    /// OpenAI Realtime's <c>session.update</c>. We can't change <c>system_instruction</c>
    /// mid-session, so this is a no-op. A new system prompt only takes effect on the next
    /// session.
    /// </summary>
    public Task RefreshSystemPromptAsync(CancellationToken ct = default)
    {
        _logger.LogDebug(
            "RefreshSystemPromptAsync ignored for Gemini Live session {ConversationId} — protocol does not support mid-session prompt updates",
            _conversation.Id);
        return Task.CompletedTask;
    }

    public async Task AddUserMessageAsync(string text, CancellationToken ct = default)
    {
        // Push a user-role turn with turn_complete=false so the model treats it as accumulated
        // input and waits for more before responding. The next turn_complete=true (from real
        // user audio or from RequestResponseAsync) commits the conversation and triggers a reply.
        await SendMessageAsync(new GeminiClientMessage
        {
            ClientContent = new GeminiClientContent
            {
                Turns = [new { role = "user", parts = new[] { new { text } } }],
                TurnComplete = false
            }
        }, ct);
    }

    public async Task RequestResponseAsync(CancellationToken ct = default)
    {
        // Empty turns + turn_complete=true commits whatever was previously added via
        // AddUserMessageAsync and asks the model to respond. Gemini Live has no separate
        // response.create event — turn_complete is the trigger.
        await SendMessageAsync(new GeminiClientMessage
        {
            ClientContent = new GeminiClientContent
            {
                Turns = [],
                TurnComplete = true
            }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _reconnectTimer?.Dispose();
        await _sessionCts.CancelAsync();

        try { await _receiveTask; }
        catch (OperationCanceledException) { }

        await CloseWebSocketAsync(_ws);
        _ws.Dispose();
        _sendLock.Dispose();
        _receiveCts.Dispose();
        _sessionCts.Dispose();
        _channel.Writer.TryComplete();
    }

    // ── Connection management ────────────────────────────────────────────────

    private async Task EstablishConnectionAsync(CancellationToken ct)
    {
        var uri = new Uri(
            $"wss://generativelanguage.googleapis.com/ws/" +
            $"google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent" +
            $"?key={_config.ApiKey}");

        _logger.LogDebug("Connecting to Gemini Live for conversation {ConversationId}", _conversation.Id);
        await _ws.ConnectAsync(uri, ct);

        _setupComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

        // Send setup as first message; wait for setup_complete before returning
        await SendSetupAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _setupComplete.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Gemini Live session setup timed out after 30 seconds.");
        }

        _logger.LogDebug("Gemini Live session ready for conversation {ConversationId}", _conversation.Id);
    }

    private async Task SendSetupAsync(CancellationToken ct)
    {
        // Re-fetch the conversation from the store so mid-session mutations (activate_skill,
        // set_intention, etc.) are reflected on reconnect. The captured _conversation reference
        // is a snapshot from session start — stale by the time tools mutate state via the store.
        var live = _agentLogic.GetConversation(_conversation.Id) ?? _conversation;
        var systemPrompt = _agentLogic.GetSystemPrompt(live.Id, live.Source, voice: true, live.ActiveSkills, live.Intention);
        // Realtime sessions don't replay conversation history on connect, so the compaction
        // summary (which holds long-term context for older turns) goes into the system prompt
        // rather than as a separate user item. Live post-cut messages are seeded separately
        // by the caller via AddUserMessageAsync.
        if (!string.IsNullOrWhiteSpace(live.Context))
            systemPrompt += "\n\n<summary>\n" + live.Context.Trim() + "\n</summary>";

        IReadOnlyList<GeminiToolSet>? tools = null;
        if (_agentLogic.Tools.Count > 0)
        {
            tools =
            [
                new GeminiToolSet
                {
                    FunctionDeclarations = _agentLogic.Tools.Select(t => new GeminiFunctionDeclaration
                    {
                        Name = t.Name,
                        Description = t.Description,
                        Parameters = t.Parameters
                    }).ToList()
                }
            ];
        }

        await SendMessageAsync(new GeminiClientMessage
        {
            Setup = new GeminiSetup
            {
                Model = _conversation.Model,
                GenerationConfig = new GeminiGenerationConfig
                {
                    ResponseModalities = ["AUDIO"],
                    SpeechConfig = new GeminiSpeechConfig
                    {
                        VoiceConfig = new GeminiVoiceConfig
                        {
                            PrebuiltVoiceConfig = new GeminiPrebuiltVoiceConfig
                            {
                                VoiceName = _config.Voice ?? "Puck"
                            }
                        }
                    }
                },
                SystemInstruction = string.IsNullOrEmpty(systemPrompt) ? null : new GeminiSystemInstruction
                {
                    Parts = [new GeminiTextPart { Text = systemPrompt }]
                },
                Tools = tools
            }
        }, ct);
    }

    private void ArmReconnectTimer()
    {
        _reconnectTimer?.Dispose();
        var delay = TimeSpan.FromMinutes(_config.ReconnectAfterMinutes);
        _reconnectTimer = new Timer(_ => _ = ReconnectAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private async Task ReconnectAsync()
    {
        // Only one reconnect at a time
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
        if (_sessionCts.IsCancellationRequested) return;

        _logger.LogInformation("Reconnecting Gemini Live session for conversation {ConversationId}", _conversation.Id);
        try
        {
            // Cancel the old receive loop
            await _receiveCts.CancelAsync();
            try { await _receiveTask; } catch (OperationCanceledException) { }
            _receiveCts.Dispose();
            _receiveCts = new CancellationTokenSource();

            // Close and replace the WebSocket
            await CloseWebSocketAsync(_ws);
            _ws.Dispose();
            _ws = new ClientWebSocket();

            await EstablishConnectionAsync(_sessionCts.Token);
            ArmReconnectTimer();

            _logger.LogInformation("Gemini Live session reconnected for conversation {ConversationId}", _conversation.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Gemini Live reconnect failed for conversation {ConversationId}", _conversation.Id);
            // Surface error to the consumer
            await _channel.Writer.WriteAsync(new SessionError($"Reconnect failed: {ex.Message}"));
        }
        finally
        {
            Volatile.Write(ref _reconnecting, 0);
        }
    }

    private static async Task CloseWebSocketAsync(ClientWebSocket ws)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* best-effort */ }
        }
    }

    // ── Send helpers ─────────────────────────────────────────────────────────

    private async Task SendMessageAsync(GeminiClientMessage msg, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(msg);
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(json, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task SendToolResponseAsync(string id, string name, string result, CancellationToken ct)
    {
        await SendMessageAsync(new GeminiClientMessage
        {
            ToolResponse = new GeminiToolResponse
            {
                FunctionResponses =
                [
                    new GeminiFunctionResponse
                    {
                        Id = id,
                        Name = name,
                        Response = new GeminiFunctionResponseBody
                        {
                            // Wrap in an object so Gemini can reference it as structured output
                            Output = new { result }
                        }
                    }
                ]
            }
        }, ct);
        // Note: Gemini resumes automatically after receiving tool response — no response.create needed
    }

    // ── Receive loop ─────────────────────────────────────────────────────────

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

                var msg = JsonSerializer.Deserialize<GeminiServerMessage>(buffer.WrittenSpan, JsonOptions);
                if (msg is null) continue;

                await HandleServerMessageAsync(msg, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Gemini Live WebSocket connection lost for conversation {ConversationId}", _conversation.Id);
        }
        finally
        {
            // Only complete the channel if the session itself is ending (not just a reconnect)
            if (_sessionCts.IsCancellationRequested)
                _channel.Writer.TryComplete();
        }
    }

    private async Task HandleServerMessageAsync(GeminiServerMessage msg, CancellationToken ct)
    {
        // setup_complete — signal ConnectAsync to continue
        if (msg.SetupComplete.HasValue)
        {
            SessionId = Guid.NewGuid().ToString(); // Gemini doesn't provide an explicit session ID
            _conversation.VoiceSessionId = SessionId;
            _conversation.VoiceSessionOpen = true;
            _agentLogic.UpdateConversation(_conversation);
            _setupComplete.TrySetResult();
            return;
        }

        // goAway — advance notice that the session will terminate; reconnect proactively
        if (msg.GoAway.HasValue)
        {
            _logger.LogInformation("Gemini Live goAway received for conversation {ConversationId} — reconnecting", _conversation.Id);
            _ = Task.Run(ReconnectAsync, _sessionCts.Token);
            return;
        }

        // error
        if (msg.Error is { } error)
        {
            var errorMsg = error.Message ?? $"Gemini error code {error.Code}";
            _logger.LogError("Gemini Live error for conversation {ConversationId}: {Error}", _conversation.Id, errorMsg);
            await _channel.Writer.WriteAsync(new SessionError(errorMsg), ct);
            return;
        }

        // serverContent
        if (msg.ServerContent is { } content)
        {
            await HandleServerContentAsync(content, ct);
            return;
        }

        // toolCall
        if (msg.ToolCall is { } toolCall)
        {
            await HandleToolCallAsync(toolCall, ct);
        }
    }

    private async Task HandleServerContentAsync(GeminiServerContent content, CancellationToken ct)
    {
        // Audio parts from the model turn
        if (content.ModelTurn?.Parts is { } parts)
        {
            foreach (var part in parts)
            {
                if (part.InlineData is { MimeType: { } mime, Data: { } data } &&
                    mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    var audio = Convert.FromBase64String(data);
                    await _channel.Writer.WriteAsync(new AudioDelta(audio), ct);
                }
            }
        }

        // Output transcription (assistant speech) — stream delta and accumulate for persistence
        if (content.OutputTranscription?.Text is { Length: > 0 } outputText)
        {
            _assistantTranscript.Append(outputText);
            await _channel.Writer.WriteAsync(new TranscriptDelta(outputText, TranscriptSource.Assistant), ct);
        }

        // Input transcription (user speech) — stream delta and accumulate for persistence
        if (content.InputTranscription?.Text is { Length: > 0 } inputText)
        {
            _userTranscript.Append(inputText);
            await _channel.Writer.WriteAsync(new TranscriptDelta(inputText, TranscriptSource.User), ct);
        }

        // Turn complete — flush audio, emit full transcripts, persist to conversation history
        if (content.TurnComplete == true)
        {
            await _channel.Writer.WriteAsync(new AudioDone(), ct);

            if (_assistantTranscript.Length > 0)
            {
                var assistantText = _assistantTranscript.ToString();
                _assistantTranscript.Clear();
                await _channel.Writer.WriteAsync(new TranscriptDone(assistantText, TranscriptSource.Assistant), ct);
                _agentLogic.AddMessage(_conversation.Id, new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    ConversationId = _conversation.Id,
                    Role = "assistant",
                    Content = assistantText
                });
            }

            if (_userTranscript.Length > 0)
            {
                var userText = _userTranscript.ToString();
                _userTranscript.Clear();
                await _channel.Writer.WriteAsync(new TranscriptDone(userText, TranscriptSource.User), ct);
                _agentLogic.AddMessage(_conversation.Id, new Message
                {
                    Id = Guid.NewGuid().ToString(),
                    ConversationId = _conversation.Id,
                    Role = "user",
                    Content = userText
                });
            }
        }

        // Interrupted — user barged in; discard any partial assistant turn transcript
        if (content.Interrupted == true)
        {
            _assistantTranscript.Clear();
            _userTranscript.Clear();
            await _channel.Writer.WriteAsync(new SpeechStarted(), ct);
        }
    }

    private async Task HandleToolCallAsync(GeminiToolCall toolCall, CancellationToken ct)
    {
        if (toolCall.FunctionCalls is not { Count: > 0 } calls) return;

        foreach (var call in calls)
        {
            var name = call.Name ?? "";
            var callId = call.Id ?? Guid.NewGuid().ToString();
            // Gemini sends args as a JsonElement object — serialize to JSON string for IAgentLogic
            var arguments = call.Args.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : JsonSerializer.Serialize(call.Args);
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
                ToolCalls = toolCalls
            });

            // Capture loop variables for the closure
            var capturedName = name;
            var capturedCallId = callId;
            var capturedArguments = arguments;

            _ = Task.Run(async () =>
            {
                // Emit thinking-cue before the tool runs so the endpoint can push a placeholder.
                await _channel.Writer.WriteAsync(new VoiceToolCallStarted(capturedName, capturedCallId), ct);
                string? completionResult = null;
                try
                {
                    _logger.LogDebug("Executing Gemini voice tool {ToolName} for conversation {ConversationId}",
                        capturedName, conversationId);
                    var result = await _agentLogic.ExecuteToolAsync(conversationId, capturedName, capturedArguments, ct);
                    completionResult = result;

                    _agentLogic.AddMessage(conversationId, new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = conversationId,
                        Role = "tool",
                        Content = ToolResultSummary.Create(capturedName, result),
                        ToolCallId = capturedCallId
                    });

                    await SendToolResponseAsync(capturedCallId, capturedName, result, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Gemini voice tool {ToolName} failed for conversation {ConversationId}",
                        capturedName, conversationId);
                    var errorResult = JsonSerializer.Serialize(new { error = ex.Message });
                    completionResult = errorResult;

                    _agentLogic.AddMessage(conversationId, new Message
                    {
                        Id = Guid.NewGuid().ToString(),
                        ConversationId = conversationId,
                        Role = "tool",
                        Content = ToolResultSummary.Create(capturedName, errorResult),
                        ToolCallId = capturedCallId
                    });

                    await SendToolResponseAsync(capturedCallId, capturedName, errorResult, ct);
                }
                finally
                {
                    // Always emit the completed cue — even on cancellation — so the browser pump
                    // never gets stuck on thinking_started. TryWrite is safe during channel completion.
                    _channel.Writer.TryWrite(new VoiceToolCallCompleted(capturedCallId, completionResult ?? "cancelled"));
                }
            }, ct);
        }
    }
}
