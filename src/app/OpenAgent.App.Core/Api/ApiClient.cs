using System.Net;
using System.Net.Http.Json;
using OpenAgent.App.Core.Models;
using OpenAgent.App.Core.Services;

namespace OpenAgent.App.Core.Api;

/// <summary>HTTP client to the agent's REST endpoints. Reads BaseUrl + Token from <see cref="ICredentialStore"/> on every call.</summary>
public sealed class ApiClient : IApiClient
{
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;

    /// <summary>Create the client with a shared <see cref="HttpClient"/> and a credential source.</summary>
    public ApiClient(HttpClient http, ICredentialStore credentials)
    {
        _http = http;
        _credentials = credentials;
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
        // Anonymous type serialized through JsonOptions.Default — snake_case policy is a no-op for the lowercase "intention" key.
        var body = JsonContent.Create(new { intention }, options: JsonOptions.Default);
        return SendAsync<object>(HttpMethod.Patch, $"api/conversations/{Uri.EscapeDataString(conversationId)}", body, ct);
    }

    // Build the request, attach X-Api-Key, send, and translate failures into typed exceptions.
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        var creds = await _credentials.LoadAsync(ct) ?? throw new InvalidOperationException("No credentials");
        var req = new HttpRequestMessage(method, new Uri(new Uri(creds.BaseUrl), path));
        req.Headers.Add("X-Api-Key", creds.Token);
        if (body is not null) req.Content = body;

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new NetworkException(ex.Message, ex); }

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
