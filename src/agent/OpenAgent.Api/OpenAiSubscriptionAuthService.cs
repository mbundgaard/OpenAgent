using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenAgent.Api;

/// <summary>
/// Creates OpenAI OAuth authorize URLs and exchanges callback URLs for access tokens.
/// </summary>
public sealed class OpenAiSubscriptionAuthService(IHttpClientFactory httpClientFactory)
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string Scope = "openid profile email offline_access";

    private readonly ConcurrentDictionary<string, PendingState> _pending = new();

    public string CreateAuthorizationUrl(string redirectUri)
    {
        CleanupExpired();

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

        _pending[state] = new PendingState(verifier, redirectUri, DateTimeOffset.UtcNow.AddMinutes(10));

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(AuthorizeUrl, query!);
    }

    public async Task<string> ExchangeCallbackUrlAsync(string callbackUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callback))
            throw new InvalidOperationException("callbackUrl must be an absolute URL.");

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(callback.Query);
        var code = query.TryGetValue("code", out var c) ? c.ToString() : null;
        var state = query.TryGetValue("state", out var s) ? s.ToString() : null;

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            throw new InvalidOperationException("callbackUrl must contain code and state query parameters.");

        if (!_pending.TryRemove(state, out var pending))
            throw new InvalidOperationException("Invalid or expired OAuth state. Start login again.");

        if (pending.ExpiresAt < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("OAuth state expired. Start login again.");

        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = pending.Verifier,
            ["redirect_uri"] = pending.RedirectUri
        }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI token exchange failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var tokenProp) || string.IsNullOrWhiteSpace(tokenProp.GetString()))
            throw new InvalidOperationException("OpenAI token exchange response missing access_token.");

        return tokenProp.GetString()!;
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in _pending)
        {
            if (value.ExpiresAt < now)
                _pending.TryRemove(key, out _);
        }
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed record PendingState(string Verifier, string RedirectUri, DateTimeOffset ExpiresAt);
}
