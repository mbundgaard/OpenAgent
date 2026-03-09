using System.Net;

namespace OpenAgent.Tools.WebFetch;

/// <summary>
/// Abstraction over DNS resolution — allows testing SSRF protection without real DNS lookups.
/// </summary>
public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken ct = default);
}

/// <summary>
/// Default DNS resolver using system DNS.
/// </summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken ct = default)
        => Dns.GetHostAddressesAsync(hostname, ct);
}
