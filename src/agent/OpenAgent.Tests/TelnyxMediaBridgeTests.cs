using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Channel.Telnyx;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Voice;
using OpenAgent.Tests.Fakes;
using Xunit;

namespace OpenAgent.Tests;

/// <summary>
/// Integration tests for the Telnyx media bridge audio passthrough (Task 18). Exercises the
/// real WS streaming endpoint against a faked <see cref="ILlmVoiceProvider"/> so we can drive
/// inbound media frames into the session and pump <see cref="VoiceEvent"/>s back out.
/// Barge-in / thinking / hangup behaviors land in Tasks 19/20/21 and are not covered here.
/// </summary>
public class TelnyxMediaBridgeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TestConnectionId = "test-telnyx-bridge";
    private const string TestWebhookId = "bridgehook1234";
    private const string TestCallControlAppId = "telnyx-cc-app-bridge";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly RecordingHandler _recordingHandler = new();

    public TelnyxMediaBridgeTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the real voice provider + resolver with a fake so the bridge's
                // StartSessionAsync returns a controllable FakeVoiceSession.
                services.RemoveAll(typeof(ILlmVoiceProvider));
                services.RemoveAll(typeof(Func<string, ILlmVoiceProvider>));
                services.AddSingleton<TestVoiceProvider>();
                services.AddSingleton<ILlmVoiceProvider>(sp => sp.GetRequiredService<TestVoiceProvider>());
                services.AddSingleton<Func<string, ILlmVoiceProvider>>(sp =>
                    _ => sp.GetRequiredService<TestVoiceProvider>());

                // Replace the named HttpClient that TelnyxCallControlClient consumes with one
                // wired to a recording handler so hangup tests don't hit live Telnyx.
                services.AddHttpClient(nameof(TelnyxCallControlClient))
                    .ConfigurePrimaryHttpMessageHandler(() => _recordingHandler);
            });
        });
    }

    [Fact]
    public async Task InboundMedia_DecodedAndForwardedToSession()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, conversationId) = await ConnectStreamAsync(cts.Token);

        // Drive an inbound media frame into the WS — payload is the µ-law bytes 0x01..0x05.
        var audio = new byte[] { 1, 2, 3, 4, 5 };
        var frame = JsonSerializer.Serialize(new
        {
            @event = "media",
            media = new { track = "inbound", payload = Convert.ToBase64String(audio) }
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(conversationId, cts.Token);
        await WaitUntilAsync(() => session.SentAudio.Count > 0, cts.Token);
        Assert.Equal(audio, session.SentAudio[0]);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task OutboundTrack_FilteredFromInbound()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, conversationId) = await ConnectStreamAsync(cts.Token);

        // Telnyx may misconfigure stream_track and echo our own audio back as track="outbound";
        // the bridge must drop those frames so we don't loop.
        var outboundFrame = JsonSerializer.Serialize(new
        {
            @event = "media",
            media = new { track = "outbound", payload = Convert.ToBase64String(new byte[] { 9, 9, 9 }) }
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(outboundFrame), WebSocketMessageType.Text, true, cts.Token);

        // Then send a real inbound frame so we have a positive signal to wait on.
        var inboundAudio = new byte[] { 7, 7, 7 };
        var inboundFrame = JsonSerializer.Serialize(new
        {
            @event = "media",
            media = new { track = "inbound", payload = Convert.ToBase64String(inboundAudio) }
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(inboundFrame), WebSocketMessageType.Text, true, cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(conversationId, cts.Token);
        await WaitUntilAsync(() => session.SentAudio.Count > 0, cts.Token);

        // Exactly one chunk must have made it through — the outbound one was filtered.
        Assert.Single(session.SentAudio);
        Assert.Equal(inboundAudio, session.SentAudio[0]);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task DtmfFrame_DoesNotCrashBridge()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, conversationId) = await ConnectStreamAsync(cts.Token);

        var dtmfFrame = JsonSerializer.Serialize(new
        {
            @event = "dtmf",
            dtmf = new { digit = "5" }
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(dtmfFrame), WebSocketMessageType.Text, true, cts.Token);

        // Follow up with an inbound media frame to prove the bridge is still alive.
        var audio = new byte[] { 4, 2 };
        var mediaFrame = JsonSerializer.Serialize(new
        {
            @event = "media",
            media = new { track = "inbound", payload = Convert.ToBase64String(audio) }
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(mediaFrame), WebSocketMessageType.Text, true, cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(conversationId, cts.Token);
        await WaitUntilAsync(() => session.SentAudio.Count > 0, cts.Token);
        Assert.Equal(audio, session.SentAudio[0]);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task OutboundAudioDelta_EncodedAndSentAsMediaFrame()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, conversationId) = await ConnectStreamAsync(cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(conversationId, cts.Token);

        var audio = new byte[] { 0xAA, 0xBB, 0xCC };
        session.Emit(new AudioDelta(audio));

        var msg = await ReceiveTextMessageAsync(ws, cts.Token);
        using var doc = JsonDocument.Parse(msg);
        Assert.Equal("media", doc.RootElement.GetProperty("event").GetString());
        var payload = doc.RootElement.GetProperty("media").GetProperty("payload").GetString();
        Assert.NotNull(payload);
        Assert.Equal(audio, Convert.FromBase64String(payload!));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task SpeechStarted_SendsClearFrame_AndCancelsResponse()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, conversationId) = await ConnectStreamAsync(cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(conversationId, cts.Token);

        // Barge-in: user starts speaking while the agent is mid-response. Bridge must flush any
        // buffered TTS via Telnyx's clear frame and tell the LLM session to abort the response.
        session.Emit(new SpeechStarted());

        var msg = await ReceiveTextMessageAsync(ws, cts.Token);
        using var doc = JsonDocument.Parse(msg);
        Assert.Equal("clear", doc.RootElement.GetProperty("event").GetString());

        await WaitUntilAsync(() => session.CancelCalled, cts.Token);
        Assert.True(session.CancelCalled);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task PendingHangup_AfterAudioDelta_HangsUpOnAudioDone()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, conversationId) = await ConnectStreamAsync(cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(conversationId, cts.Token);
        var bridge = await WaitForBridgeAsync(conversationId, cts.Token);

        // Clean farewell case: agent calls EndCallTool (sets pending), speaks farewell, finishes.
        bridge.SetPendingHangup();
        session.Emit(new AudioDelta(new byte[] { 0, 1, 2 }));
        await Task.Delay(20, cts.Token);
        session.Emit(new AudioDone());

        await WaitUntilAsync(() => _recordingHandler.HangupCalled, cts.Token);
        Assert.True(_recordingHandler.HangupCalled);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task PendingHangup_NoAudioInFlight_HangsUpAfter500ms()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, _, conversationId) = await ConnectStreamAsync(cts.Token);

        var bridge = await WaitForBridgeAsync(conversationId, cts.Token);

        // Model already finished or didn't speak — no AudioDelta after SetPendingHangup. The
        // 500ms early-exit timer must fire and hang up the call.
        bridge.SetPendingHangup();

        // Generous 2s window for the 500ms timer plus dispatch slack.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        waitCts.CancelAfter(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => _recordingHandler.HangupCalled, waitCts.Token);
        Assert.True(_recordingHandler.HangupCalled);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task PendingHangup_ModelMisbehaves_HangsUpAfter5s_Hard()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (ws, fakeProvider, conversationId) = await ConnectStreamAsync(cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(conversationId, cts.Token);
        var bridge = await WaitForBridgeAsync(conversationId, cts.Token);

        // Hard fallback case: agent starts speaking but AudioDone never arrives (model stalled or
        // API hiccup). The 5s total cap must trip and hang up regardless.
        bridge.SetPendingHangup();
        session.Emit(new AudioDelta(new byte[] { 0 }));
        // No AudioDone emitted.

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        waitCts.CancelAfter(TimeSpan.FromSeconds(7));
        await WaitUntilAsync(() => _recordingHandler.HangupCalled, waitCts.Token);
        Assert.True(_recordingHandler.HangupCalled);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task WsClose_DisposesSession_AndUnregistersBridge_AndReturnsRunAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, conversationId) = await ConnectStreamAsync(cts.Token);

        // Wait for the bridge to register and the voice session to be created so we know the
        // bridge is fully running before we tear it down from the client side.
        var session = await fakeProvider.WaitForSessionAsync(conversationId, cts.Token);
        _ = await WaitForBridgeAsync(conversationId, cts.Token);

        var registry = _factory.Services.GetRequiredService<TelnyxBridgeRegistry>();
        Assert.True(registry.TryGet(conversationId, out _));

        // Caller hangs up: client closes the WebSocket. The bridge's RunAsync.finally must
        // dispose the voice session and unregister from the registry, returning cleanly.
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        // Poll for teardown — registry entry gone AND session disposed. 2s window is plenty.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        waitCts.CancelAfter(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => !registry.TryGet(conversationId, out _) && session.DisposeCalled,
            waitCts.Token);

        Assert.False(registry.TryGet(conversationId, out _));
        Assert.True(session.DisposeCalled);
    }

    [Fact]
    public async Task Dtmf_MidWindow_SwapsToExtensionConversation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, throwawayId) = await ConnectStreamAsync(cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(throwawayId, cts.Token);
        var bridge = await WaitForBridgeAsync(throwawayId, cts.Token);

        // Caller pressed "1" via comma-extension dialing — bridge resolves {from},1, asks the
        // session to rebind, re-keys the registry, deletes the throwaway.
        bridge.OnDtmfReceived("1");

        // Wait for the swap to land — RebindConversationAsync recorded on the fake session and
        // the throwaway removed from the conversation store.
        var conversationStore = _factory.Services.GetRequiredService<IConversationStore>();
        await WaitUntilAsync(
            () => session.ReboundConversationIds.Count > 0
                  && conversationStore.Get(throwawayId) is null,
            cts.Token);

        var extensionId = session.ReboundConversationIds.Single();
        Assert.NotEqual(throwawayId, extensionId);

        var extension = conversationStore.Get(extensionId);
        Assert.NotNull(extension);
        Assert.EndsWith(" ext.1", extension!.DisplayName);

        // Registry re-keyed: lookup by call_control_id resolves to the same bridge, lookup by
        // extension id finds the bridge, throwaway lookup is gone.
        var registry = _factory.Services.GetRequiredService<TelnyxBridgeRegistry>();
        Assert.True(registry.TryGet(extensionId, out var registered));
        Assert.Same(bridge, registered);
        Assert.False(registry.TryGet(throwawayId, out _));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task Dtmf_BeforeWsConnects_BuffersOnPendingBridge_AndDrainsOnStart()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await SetupRunningConnectionAsync();
        var manager = _factory.Services.GetRequiredService<IConnectionManager>();
        var telnyxProvider = manager.GetProviders()
            .Select(p => p.Provider)
            .OfType<TelnyxChannelProvider>()
            .First(p => p.ConnectionId == TestConnectionId);

        var conversationStore = _factory.Services.GetRequiredService<IConversationStore>();
        var callControlId = "call-" + Guid.NewGuid().ToString("N");
        var callSessionId = Guid.NewGuid().ToString("N");
        const string from = "+4520000000";
        var throwaway = conversationStore.FindOrCreateChannelConversation(
            channelType: "telnyx",
            connectionId: TestConnectionId,
            channelChatId: $"{from}:{callSessionId}",
            source: "telnyx",
            provider: "azure-openai-voice",
            model: "test-model");

        // Register the pending entry, enqueue a digit on it BEFORE the WS connects, then connect.
        var pendingCts = new CancellationTokenSource();
        var pending = new PendingBridge(
            CallControlId: callControlId,
            CallSessionId: callSessionId,
            From: from,
            VoiceProviderKey: "azure-openai-voice",
            Cts: pendingCts);
        Assert.True(telnyxProvider.TryRegisterPending(callControlId, pending));
        pending.PendingDtmf.Enqueue("2");

        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/api/webhook/telnyx/{TestWebhookId}/stream?call={Uri.EscapeDataString(callControlId)}"),
            cts.Token);

        var fakeProvider = _factory.Services.GetRequiredService<TestVoiceProvider>();
        var session = await fakeProvider.WaitForSessionAsync(throwaway.Id, cts.Token);

        // Bridge drains the pre-WS queue and performs the swap to {from},2 immediately on start.
        await WaitUntilAsync(() => session.ReboundConversationIds.Count > 0, cts.Token);
        var extensionId = session.ReboundConversationIds.Single();
        var extension = conversationStore.Get(extensionId);
        Assert.NotNull(extension);
        Assert.EndsWith(" ext.2", extension!.DisplayName);
        Assert.Null(conversationStore.Get(throwaway.Id));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task Dtmf_OnGeminiNotSupported_LogsWarning_AndKeepsThrowaway()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, throwawayId) = await ConnectStreamAsync(cts.Token);

        // Configure the fake session to throw NotSupportedException from Rebind — same shape
        // as Gemini Live's implementation.
        var session = await fakeProvider.WaitForSessionAsync(throwawayId, cts.Token);
        session.RebindHook = _ => throw new NotSupportedException("test: gemini cannot rebind");

        var bridge = await WaitForBridgeAsync(throwawayId, cts.Token);
        bridge.OnDtmfReceived("1");

        // Give the bridge a moment to attempt the swap and log; the throwaway must remain.
        await Task.Delay(200, cts.Token);

        var conversationStore = _factory.Services.GetRequiredService<IConversationStore>();
        Assert.NotNull(conversationStore.Get(throwawayId));

        var registry = _factory.Services.GetRequiredService<TelnyxBridgeRegistry>();
        Assert.True(registry.TryGet(throwawayId, out _));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task Dtmf_SecondDigit_Ignored_SingleShot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ws, fakeProvider, throwawayId) = await ConnectStreamAsync(cts.Token);

        var session = await fakeProvider.WaitForSessionAsync(throwawayId, cts.Token);
        var bridge = await WaitForBridgeAsync(throwawayId, cts.Token);

        bridge.OnDtmfReceived("1");
        await WaitUntilAsync(() => session.ReboundConversationIds.Count > 0, cts.Token);

        // Gate is single-shot — second digit must not produce another swap.
        bridge.OnDtmfReceived("2");
        await Task.Delay(150, cts.Token);

        Assert.Single(session.ReboundConversationIds);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    /// <summary>
    /// Polls the bridge registry until the bridge for the given conversation has registered itself.
    /// </summary>
    private async Task<TelnyxMediaBridge> WaitForBridgeAsync(string conversationId, CancellationToken ct)
    {
        var registry = _factory.Services.GetRequiredService<TelnyxBridgeRegistry>();
        for (var i = 0; i < 200; i++)
        {
            if (registry.TryGet(conversationId, out var bridge) && bridge is TelnyxMediaBridge b)
                return b;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException($"No bridge registered for conversation {conversationId}.");
    }

    /// <summary>
    /// Spins up a Telnyx connection in the host, seeds a Phone conversation, registers a pending
    /// bridge for a synthetic call control id, and connects a WebSocket to the streaming endpoint.
    /// Returns the live WS, the fake voice provider, and the conversation id so tests can assert
    /// against the resulting <see cref="FakeVoiceSession"/>.
    /// </summary>
    private async Task<(WebSocket Ws, TestVoiceProvider Provider, string ConversationId)> ConnectStreamAsync(
        CancellationToken ct)
    {
        await SetupRunningConnectionAsync();

        var manager = _factory.Services.GetRequiredService<IConnectionManager>();
        var telnyxProvider = manager.GetProviders()
            .Select(p => p.Provider)
            .OfType<TelnyxChannelProvider>()
            .First(p => p.ConnectionId == TestConnectionId);

        // The bridge creates its own throwaway conversation on WS connect, keyed on
        // {From}:{CallSessionId}. We pre-seed that exact key so FindOrCreate reuses it and we
        // can locate the conversation deterministically post-connect.
        var conversationStore = _factory.Services.GetRequiredService<IConversationStore>();
        var callControlId = "call-" + Guid.NewGuid().ToString("N");
        var callSessionId = Guid.NewGuid().ToString("N");
        const string from = "+4520000000";
        var conversation = conversationStore.FindOrCreateChannelConversation(
            channelType: "telnyx",
            connectionId: TestConnectionId,
            channelChatId: $"{from}:{callSessionId}",
            source: "telnyx",
            provider: "azure-openai-voice",
            model: "test-model");

        // Pre-register the pending bridge so the streaming endpoint can dequeue it.
        var pendingCts = new CancellationTokenSource();
        var pending = new PendingBridge(
            CallControlId: callControlId,
            CallSessionId: callSessionId,
            From: from,
            VoiceProviderKey: conversation.Provider,
            Cts: pendingCts);
        Assert.True(telnyxProvider.TryRegisterPending(callControlId, pending));

        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/api/webhook/telnyx/{TestWebhookId}/stream?call={Uri.EscapeDataString(callControlId)}"),
            ct);

        var fakeProvider = _factory.Services.GetRequiredService<TestVoiceProvider>();
        return (ws, fakeProvider, conversation.Id);
    }

    private async Task SetupRunningConnectionAsync()
    {
        var store = _factory.Services.GetRequiredService<IConnectionStore>();
        var manager = _factory.Services.GetRequiredService<IConnectionManager>();

        // Idempotent — re-running tests in the same fixture must not re-register
        if (manager.GetProviders().Any(p => p.ConnectionId == TestConnectionId))
            return;

        var config = JsonSerializer.SerializeToElement(new
        {
            apiKey = "test-key",
            phoneNumber = "+4535150636",
            baseUrl = "https://example.com",
            callControlAppId = TestCallControlAppId,
            webhookId = TestWebhookId,
        });

        store.Save(new Connection
        {
            Id = TestConnectionId,
            Name = "Test Telnyx Bridge",
            Type = "telnyx",
            Enabled = true,
            ConversationId = "unused",
            Config = config,
        });

        await manager.StartConnectionAsync(TestConnectionId, default);
    }

    /// <summary>Reads a single complete text message from the WebSocket.</summary>
    private static async Task<string> ReceiveTextMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed before message arrived");
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return sb.ToString();
    }

    /// <summary>Polls a predicate until true or the cancellation token fires.</summary>
    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (predicate()) return;
            await Task.Delay(20, ct);
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// In-process voice provider that returns a controllable <see cref="FakeVoiceSession"/>
    /// for any conversation. Tests await <see cref="WaitForSessionAsync"/> to grab the session
    /// the bridge created and then drive it directly.
    /// </summary>
    private sealed class TestVoiceProvider : ILlmVoiceProvider
    {
        private readonly ConcurrentDictionary<string, FakeVoiceSession> _sessions = new();

        public string Key => "azure-openai-voice"; // matches voiceProvider in test agent.json
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }

        public Task<IVoiceSession> StartSessionAsync(
            Conversation conversation,
            VoiceSessionOptions? options = null,
            CancellationToken ct = default)
        {
            var session = new FakeVoiceSession();
            _sessions[conversation.Id] = session;
            return Task.FromResult<IVoiceSession>(session);
        }

        public async Task<FakeVoiceSession> WaitForSessionAsync(string conversationId, CancellationToken ct)
        {
            for (var i = 0; i < 200; i++)
            {
                if (_sessions.TryGetValue(conversationId, out var session))
                    return session;
                await Task.Delay(20, ct);
            }
            throw new TimeoutException($"No fake voice session created for conversation {conversationId}.");
        }
    }

    /// <summary>
    /// HTTP message handler that records all outgoing requests and returns 200 OK without hitting
    /// the network. Used by the hangup state machine tests so we can assert the bridge POSTed to
    /// Telnyx's hangup action without making a live API call.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentBag<string> _requestUris = new();

        public IReadOnlyCollection<string> AllRequestUris => _requestUris;
        public bool HangupCalled => _requestUris.Any(u => u.Contains("/actions/hangup"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _requestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
