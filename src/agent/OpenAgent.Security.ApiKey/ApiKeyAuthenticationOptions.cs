using Microsoft.AspNetCore.Authentication;

namespace OpenAgent.Security.ApiKey;

/// <summary>
/// Options for API key authentication — holds the expected key value.
/// </summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// The expected API key value. Requests must send this in the X-Api-Key header.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
