using System.Diagnostics;
using System.Net;
using System.Text.Json;
using OpenAgent.Tools.WebFetch;

namespace OpenAgent.Tests;

/// <summary>
/// Pinning tests for <see cref="WebFetchTool"/> cancellation behavior. The Telnyx phone bridge
/// cancels its CTS during teardown — in-flight tool calls must unblock promptly so the call
/// doesn't drag on. WebFetchTool catches <see cref="TaskCanceledException"/> and returns a
/// failure JSON instead of throwing; either way, the call has to complete fast.
/// </summary>
public class WebFetchToolCancellationTests
{
    [Fact]
    public async Task ExecuteAsync_HonoursCancellationToken_ReturnsPromptly()
    {
        // Handler that hangs forever unless its incoming CT is signalled. Mirrors how a real
        // remote endpoint would behave when we cancel the request mid-flight.
        var handler = new HangingHandler();
        var httpClient = new HttpClient(handler);
        var dnsResolver = new PublicAddressDnsResolver();
        var tool = new WebFetchTool(httpClient, dnsResolver);

        using var cts = new CancellationTokenSource();

        var sw = Stopwatch.StartNew();
        var task = tool.ExecuteAsync("""{"url": "https://example.com"}""", "conv-1", cts.Token);

        // Give SendAsync a moment to enter the awaiting state, then cancel.
        await Task.Delay(50);
        cts.Cancel();

        // The tool must return within ~1s of cancellation (proves CT was threaded through).
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        sw.Stop();

        Assert.Same(task, completed);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"WebFetch did not honour cancellation in time (took {sw.ElapsedMilliseconds}ms)");

        // Tool surfaces cancellation as a structured failure, not a thrown exception.
        var json = await task;
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    /// <summary>
    /// Handler whose <c>SendAsync</c> awaits the incoming <see cref="CancellationToken"/>. If the
    /// token is never signalled the request hangs indefinitely; if cancelled it surfaces as
    /// <see cref="TaskCanceledException"/> just like a real <see cref="HttpClient"/> would.
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class PublicAddressDnsResolver : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken ct = default)
            => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    }
}
