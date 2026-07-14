using System.Net;
using System.Text;

namespace OpenAgent.Tests.Fakes;

/// <summary>
/// Minimal HttpMessageHandler stub for provider-level HTTP tests. Returns one queued response
/// per call (FIFO) and records every outbound request's method, URL, and body so tests can
/// assert on exactly what a provider sent to the wire — this is the seam used to prove the
/// heartbeat-nudge ordering fix actually renders correctly in the outbound JSON, not just that
/// the runner passed <c>persistUserMessage: false</c> through.
///
/// Not registered via DI — text providers build their own internal <see cref="HttpClient"/> in
/// <c>Configure()</c> with no injection point, so tests install this by reflecting the private
/// <c>_httpClient</c> field after calling <c>Configure()</c>. See
/// <see cref="TextProviderHttpTestHelper.InstallStubHandler"/>.
/// </summary>
public sealed class QueuedStubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    /// <summary>Every request this handler has seen, in send order.</summary>
    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>Queues a plain-text/event-stream response with the given SSE body.</summary>
    public void EnqueueSseResponse(HttpStatusCode statusCode, string sseBody)
    {
        _responses.Enqueue(() => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
        });
    }

    /// <summary>Queues a plain JSON error body response (non-2xx, e.g. context-overflow 400).</summary>
    public void EnqueueJsonResponse(HttpStatusCode statusCode, string jsonBody)
    {
        _responses.Enqueue(() => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(request.Method.Method, request.RequestUri?.ToString() ?? "", body));

        if (_responses.Count == 0)
            throw new InvalidOperationException("QueuedStubHttpMessageHandler: no more responses queued.");

        return _responses.Dequeue()();
    }

    public sealed record CapturedRequest(string Method, string Url, string Body);
}
