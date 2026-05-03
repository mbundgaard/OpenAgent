using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.App.Core.Logging;
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Core.Api;

/// <summary>HTTP client to the agent's REST endpoints. Reads BaseUrl + Token from <see cref="IConnectionStore"/> on every call.</summary>
public sealed class ApiClient : IApiClient
{
    // The path of the log-shipping endpoint itself. We must NEVER log the request/response of
    // this call — every log line would create another log line, and the buffer would saturate.
    private const string ClientLogsPath = "api/client-logs";

    private readonly HttpClient _http;
    private readonly IConnectionStore _connections;
    private readonly ILogger<ApiClient> _logger;

    /// <summary>Create the client with a shared <see cref="HttpClient"/> and a connection store.</summary>
    public ApiClient(HttpClient http, IConnectionStore connections, ILogger<ApiClient>? logger = null)
    {
        _http = http;
        _connections = connections;
        _logger = logger ?? NullLogger<ApiClient>.Instance;
    }

    /// <inheritdoc/>
    public Task<List<ConversationListItem>> GetConversationsAsync(CancellationToken ct = default)
        => SendAsync<List<ConversationListItem>>(HttpMethod.Get, "api/conversations", null, ct)!;

    /// <inheritdoc/>
    public Task DeleteConversationAsync(string conversationId, CancellationToken ct = default)
        => SendAsync<object>(HttpMethod.Delete, $"api/conversations/{Uri.EscapeDataString(conversationId)}", null, ct);

    /// <inheritdoc/>
    public Task RenameConversationAsync(string conversationId, string intention, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(intention))
            throw new ArgumentException("Intention required", nameof(intention));

        // Anonymous type serialized through JsonOptions.Default — snake_case policy is a no-op for the lowercase "intention" key.
        var body = JsonContent.Create(new { intention }, options: JsonOptions.Default);
        return SendAsync<object>(HttpMethod.Patch, $"api/conversations/{Uri.EscapeDataString(conversationId)}", body, ct);
    }

    /// <inheritdoc/>
    public async Task PostClientLogsAsync(IReadOnlyList<ClientLogLine> lines, CancellationToken ct = default)
    {
        // Mustn't throw — caller is the logging system. Network/auth failures swallowed silently;
        // a different upload attempt will retry. We never log from here ourselves (feedback loop).
        if (lines.Count == 0) return;
        var batch = new ClientLogBatch { Lines = new List<ClientLogLine>(lines) };
        var body = JsonContent.Create(batch, options: JsonOptions.Default);
        try
        {
            await SendAsync<object>(HttpMethod.Post, "api/client-logs", body, ct);
        }
        catch
        {
            // Intentional — see remarks above.
        }
    }

    // Build the request, attach X-Api-Key, send, and translate failures into typed exceptions.
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        // The log-shipping endpoint MUST be invisible to logging — see ClientLogsPath remark.
        var trace = path != ClientLogsPath;
        var sw = trace ? Stopwatch.StartNew() : null;

        var conn = await _connections.LoadActiveAsync(ct) ?? throw new InvalidOperationException("No active connection");
        var req = new HttpRequestMessage(method, new Uri(new Uri(conn.BaseUrl), path));
        req.Headers.Add("X-Api-Key", conn.Token);
        if (body is not null) req.Content = body;

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (trace) _logger.LogWarning("HTTP {Method} {Path} network error after {Ms}ms: {Error}", method.Method, path, sw!.ElapsedMilliseconds, ex.Message);
            throw new NetworkException(ex.Message, ex);
        }

        using (resp)
        {
            if (trace) _logger.LogInformation("HTTP {Method} {Path} -> {Status} in {Ms}ms", method.Method, path, (int)resp.StatusCode, sw!.ElapsedMilliseconds);

            if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new AuthRejectedException();

            if (!resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync(ct);
                throw new ApiException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {content}", (int)resp.StatusCode);
            }

            if (typeof(T) == typeof(object)) return default;
            return await resp.Content.ReadFromJsonAsync<T>(JsonOptions.Default, ct);
        }
    }
}
