using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxCallControlClientTests
{
    [Fact]
    public async Task AnswerAsync_PostsToCorrectUrl_WithBearerAuth()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var client = new TelnyxCallControlClient(http, "API_KEY", NullLogger<TelnyxCallControlClient>.Instance);

        await client.AnswerAsync("call-123", default);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.telnyx.com/v2/calls/call-123/actions/answer", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("API_KEY", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task StreamingStartAsync_PostsExpectedBody()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler);
        var client = new TelnyxCallControlClient(http, "API_KEY", NullLogger<TelnyxCallControlClient>.Instance);

        await client.StreamingStartAsync("call-123", "wss://us/stream?call=call-123", default);

        Assert.Contains("call-123/actions/streaming_start", handler.LastRequest!.RequestUri!.ToString());
        var body = await handler.LastRequest.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("wss://us/stream?call=call-123", doc.RootElement.GetProperty("stream_url").GetString());
        Assert.Equal("rtp", doc.RootElement.GetProperty("stream_bidirectional_mode").GetString());
        Assert.Equal("PCMU", doc.RootElement.GetProperty("stream_bidirectional_codec").GetString());
        Assert.Equal(8000, doc.RootElement.GetProperty("stream_bidirectional_sampling_rate").GetInt32());
        Assert.Equal("self", doc.RootElement.GetProperty("stream_bidirectional_target_legs").GetString());
        Assert.Equal("inbound_track", doc.RootElement.GetProperty("stream_track").GetString());
        Assert.NotNull(doc.RootElement.GetProperty("client_state").GetString());
    }

    [Fact]
    public async Task HangupAsync_404_TreatedAsSuccess()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound, "{\"errors\":[{\"code\":\"10005\"}]}");
        using var http = new HttpClient(handler);
        var client = new TelnyxCallControlClient(http, "API_KEY", NullLogger<TelnyxCallControlClient>.Instance);

        await client.HangupAsync("already-gone", default); // should NOT throw
    }

    [Fact]
    public async Task HangupAsync_500_Throws()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "{}");
        using var http = new HttpClient(handler);
        var client = new TelnyxCallControlClient(http, "API_KEY", NullLogger<TelnyxCallControlClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.HangupAsync("call-1", default));
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // copy request so consumer can read content
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) copy.Headers.Add(h.Key, h.Value);
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(ct);
                copy.Content = new ByteArrayContent(bytes);
                foreach (var h in request.Content.Headers) copy.Content.Headers.Add(h.Key, h.Value);
            }
            LastRequest = copy;
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
