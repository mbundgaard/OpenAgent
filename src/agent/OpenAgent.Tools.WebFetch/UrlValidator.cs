using System.Net;
using System.Net.Sockets;

namespace OpenAgent.Tools.WebFetch;

/// <summary>
/// Validates URLs for safe fetching — blocks private IPs, non-HTTP schemes, and malformed URLs.
/// </summary>
public static class UrlValidator
{
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost"
    };

    public static ValidationResult Validate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ValidationResult(false, "URL is empty", null);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new ValidationResult(false, "Invalid URL", null);

        // Only http and https
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return new ValidationResult(false, $"Unsupported scheme: {uri.Scheme}", null);

        // Block known hostnames
        if (BlockedHosts.Contains(uri.Host))
            return new ValidationResult(false, "URL points to a private or loopback address", null);

        // Check if host is an IP address
        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IsPrivateOrReserved(ip))
                return new ValidationResult(false, "URL points to a private or loopback address", null);
        }

        return new ValidationResult(true, null, uri);
    }

    /// <summary>
    /// Validates URL including DNS resolution — blocks hostnames that resolve to private/loopback IPs.
    /// </summary>
    public static async Task<ValidationResult> ValidateWithDnsAsync(string url, IDnsResolver resolver, CancellationToken ct = default)
    {
        // Run scheme/format checks first
        var result = Validate(url);
        if (!result.IsValid)
            return result;

        // If host is already a literal IP, Validate() already checked it
        if (IPAddress.TryParse(result.Uri!.Host, out _))
            return result;

        // Resolve hostname and check all returned IPs
        IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(result.Uri.Host, ct);
        }
        catch
        {
            return new ValidationResult(false, $"DNS resolution failed for {result.Uri.Host}", null);
        }

        if (addresses.Length == 0)
            return new ValidationResult(false, $"DNS resolution returned no addresses for {result.Uri.Host}", null);

        foreach (var ip in addresses)
        {
            if (IsPrivateOrReserved(ip))
                return new ValidationResult(false, "URL resolves to a private or loopback address", null);
        }

        return result;
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        // IPv6 loopback (::1)
        if (ip.Equals(IPAddress.IPv6Loopback))
            return true;

        // Map to IPv4 if possible, then check IPv4 ranges
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 link-local (fe80::/10) and unique-local (fc00::/7)
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true; // fe80::/10
            if ((bytes[0] & 0xfe) == 0xfc) return true; // fc00::/7
            return false; // Public IPv6 — allow (e.g. Cloudflare 2606:4700::/32)
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return true; // Unknown address family — block

        var v4Bytes = ip.GetAddressBytes();

        // 0.0.0.0/8
        if (v4Bytes[0] == 0) return true;

        // 10.0.0.0/8
        if (v4Bytes[0] == 10) return true;

        // 127.0.0.0/8
        if (v4Bytes[0] == 127) return true;

        // 169.254.0.0/16 (link-local, AWS metadata)
        if (v4Bytes[0] == 169 && v4Bytes[1] == 254) return true;

        // 172.16.0.0/12
        if (v4Bytes[0] == 172 && v4Bytes[1] >= 16 && v4Bytes[1] <= 31) return true;

        // 192.168.0.0/16
        if (v4Bytes[0] == 192 && v4Bytes[1] == 168) return true;

        return false;
    }
}

/// <summary>
/// Result of URL validation.
/// </summary>
public sealed record ValidationResult(bool IsValid, string? Error, Uri? Uri);
