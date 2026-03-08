using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenAgent.Security.ApiKey;

/// <summary>
/// Validates the X-Api-Key header against the configured API key.
/// Returns 401 if the key is missing or invalid.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    private const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for the API key header
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValue))
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Api-Key header."));

        var providedKey = headerValue.ToString();

        // Validate against configured key
        if (!string.Equals(providedKey, Options.ApiKey, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        // Build an authenticated identity
        var claims = new[] { new Claim(ClaimTypes.Name, "api-key-user") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
