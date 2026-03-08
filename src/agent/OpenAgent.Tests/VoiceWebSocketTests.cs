using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Voice;

namespace OpenAgent.Tests;

public class VoiceWebSocketTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public VoiceWebSocketTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmVoiceProvider));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddSingleton<FakeVoiceProvider>();
                services.AddSingleton<ILlmVoiceProvider>(sp => sp.GetRequiredService<FakeVoiceProvider>());
            });
        });
    }

    [Fact]
    public async Task VoiceEndpoint_NonWebSocket_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/ws/conversations/test-123/voice");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task VoiceEndpoint_BinaryAudio_IsForwardedToVoiceSession()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var conversationId = Guid.NewGuid().ToString();
        var ws = await ConnectVoiceWebSocketAsync(conversationId, cts.Token);
        var provider = _factory.Services.GetRequiredService<FakeVoiceProvider>();

        var audioBytes = new byte[] { 1, 2, 3, 4, 5 };
        await ws.SendAsync(audioBytes, WebSocketMessageType.Binary, true, cts.Token);

        var session = await provider.WaitForSessionAsync(conversationId, cts.Token);
        var received = await session.WaitForAudioAsync(cts.Token);

        Assert.Equal(audioBytes, received);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
    }

    [Fact]
    public async Task VoiceEndpoint_SessionEvents_AreSentBackOverWebSocket()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var conversationId = Guid.NewGuid().ToString();
        var ws = await ConnectVoiceWebSocketAsync(conversationId, cts.Token);
        var provider = _factory.Services.GetRequiredService<FakeVoiceProvider>();
        var session = await provider.WaitForSessionAsync(conversationId, cts.Token);

        await session.EnqueueEventAsync(new TranscriptDelta("hello", TranscriptSource.Assistant), cts.Token);
        var transcriptMessage = await ReceiveAsync(ws, cts.Token);
        Assert.Equal(WebSocketMessageType.Text, transcriptMessage.MessageType);
        var payload = JsonDocument.Parse(transcriptMessage.Payload);
        Assert.Equal("transcript_delta", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello", payload.RootElement.GetProperty("text").GetString());
        Assert.Equal("assistant", payload.RootElement.GetProperty("source").GetString());

        await session.EnqueueEventAsync(new AudioDelta(new byte[] { 9, 8, 7 }), cts.Token);
        var audioMessage = await ReceiveAsync(ws, cts.Token);
        Assert.Equal(WebSocketMessageType.Binary, audioMessage.MessageType);
        Assert.Equal(new byte[] { 9, 8, 7 }, audioMessage.Payload);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
    }

    private async Task<WebSocket> ConnectVoiceWebSocketAsync(string conversationId, CancellationToken ct)
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        return await wsClient.ConnectAsync(
            new Uri(_factory.Server.BaseAddress, $"/ws/conversations/{conversationId}/voice"),
            ct);
    }

    private static async Task<(WebSocketMessageType MessageType, byte[] Payload)> ReceiveAsync(
        WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, ct);
        return (result.MessageType, buffer[..result.Count]);
    }

    private sealed class FakeVoiceProvider : ILlmVoiceProvider
    {
        private readonly ConcurrentDictionary<string, FakeVoiceSession> _sessions = new();

        public string Key => "voice-provider";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }

        public Task<IVoiceSession> StartSessionAsync(Conversation conversation, CancellationToken ct = default)
        {
            var session = new FakeVoiceSession();
            _sessions[conversation.Id] = session;
            return Task.FromResult<IVoiceSession>(session);
        }

        public async Task<FakeVoiceSession> WaitForSessionAsync(string conversationId, CancellationToken ct)
        {
            for (var i = 0; i < 50; i++)
            {
                if (_sessions.TryGetValue(conversationId, out var session))
                    return session;

                await Task.Delay(20, ct);
            }

            throw new TimeoutException($"No fake voice session created for conversation {conversationId}.");
        }
    }

    private sealed class FakeVoiceSession : IVoiceSession
    {
        private readonly Channel<VoiceEvent> _events = Channel.CreateUnbounded<VoiceEvent>();
        private readonly TaskCompletionSource<byte[]> _firstAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SessionId { get; } = Guid.NewGuid().ToString();

        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
        {
            _firstAudio.TrySetResult(audio.ToArray());
            return Task.CompletedTask;
        }

        public Task CommitAudioAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task CancelResponseAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IAsyncEnumerable<VoiceEvent> ReceiveEventsAsync(CancellationToken ct = default)
            => _events.Reader.ReadAllAsync(ct);

        public async Task EnqueueEventAsync(VoiceEvent voiceEvent, CancellationToken ct)
            => await _events.Writer.WriteAsync(voiceEvent, ct);

        public async Task<byte[]> WaitForAudioAsync(CancellationToken ct)
        {
            using var registration = ct.Register(() => _firstAudio.TrySetCanceled(ct));
            return await _firstAudio.Task;
        }

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
