namespace OpenAgent.App.Core.Api;

/// <summary>Non-success HTTP response from the agent API (other than 401, which surfaces as <see cref="AuthRejectedException"/>).</summary>
public sealed class ApiException(string message, int statusCode) : Exception(message)
{
    /// <summary>HTTP status code returned by the agent.</summary>
    public int StatusCode { get; } = statusCode;
}

/// <summary>The agent rejected the configured API key (HTTP 401). Caller should prompt the user to re-pair.</summary>
public sealed class AuthRejectedException() : Exception("API key rejected by agent");

/// <summary>Wraps transport-level failures (DNS, TLS, connection refused, timeout) so callers can distinguish them from API errors.</summary>
public sealed class NetworkException(string message, Exception inner) : Exception(message, inner);
