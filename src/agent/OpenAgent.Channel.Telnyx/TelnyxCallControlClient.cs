using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Thin wrapper over the Telnyx Call Control v2 REST API for the three actions the bridge needs:
/// answer, streaming_start, hangup. Hangup is idempotent — 404/410 are treated as success because
/// multiple teardown paths legitimately race against an already-ended call.
/// </summary>
public sealed class TelnyxCallControlClient
{
    private const string ApiBase = "https://api.telnyx.com/v2";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<TelnyxCallControlClient> _logger;

    public TelnyxCallControlClient(HttpClient http, string apiKey, ILogger<TelnyxCallControlClient> logger)
    {
        _http = http;
        _apiKey = apiKey;
        _logger = logger;
    }

    public Task AnswerAsync(string callControlId, CancellationToken ct) =>
        PostAsync($"calls/{callControlId}/actions/answer", new { }, idempotent404: false, ct);

    public Task StreamingStartAsync(string callControlId, string streamUrl, CancellationToken ct)
    {
        var clientState = Convert.ToBase64String(Encoding.UTF8.GetBytes(callControlId));
        var body = new
        {
            stream_url = streamUrl,
            stream_track = "inbound_track",
            stream_bidirectional_mode = "rtp",
            stream_bidirectional_codec = "PCMU",
            stream_bidirectional_sampling_rate = 8000,
            stream_bidirectional_target_legs = "self",
            client_state = clientState
        };
        return PostAsync($"calls/{callControlId}/actions/streaming_start", body, idempotent404: false, ct);
    }

    public Task HangupAsync(string callControlId, CancellationToken ct) =>
        PostAsync($"calls/{callControlId}/actions/hangup", new { }, idempotent404: true, ct);

    private async Task PostAsync<T>(string path, T body, bool idempotent404, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/{path}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = JsonContent.Create(body);

        using var res = await _http.SendAsync(req, ct);
        if (idempotent404 && (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone))
        {
            _logger.LogDebug("Telnyx {Path} returned {Status} — treated as already-completed", path, res.StatusCode);
            return;
        }
        res.EnsureSuccessStatusCode();
    }
}
