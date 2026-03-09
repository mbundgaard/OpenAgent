using System.Net;
using OpenAgent.Tools.WebFetch;

namespace OpenAgent.Tests.WebFetch;

public class UrlValidatorTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData("not-a-url", false)]
    public void Validates_url_scheme(string url, bool expectedValid)
    {
        var result = UrlValidator.Validate(url);
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://10.0.0.1")]
    [InlineData("http://10.255.255.255")]
    [InlineData("http://192.168.1.1")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://172.31.255.255")]
    [InlineData("http://169.254.169.254")]  // AWS metadata
    [InlineData("http://0.0.0.0")]
    [InlineData("http://[::1]")]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:3000")]
    public void Blocks_private_and_loopback_addresses(string url)
    {
        var result = UrlValidator.Validate(url);
        Assert.False(result.IsValid);
        Assert.Contains("private", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://93.184.216.34")]
    [InlineData("http://8.8.8.8")]
    public void Allows_public_addresses(string url)
    {
        var result = UrlValidator.Validate(url);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Returns_normalized_uri_on_success()
    {
        var result = UrlValidator.Validate("https://example.com/path?q=1");
        Assert.True(result.IsValid);
        Assert.NotNull(result.Uri);
        Assert.Equal("https", result.Uri!.Scheme);
    }

    // --- Finding #1: DNS-based SSRF bypass ---

    [Fact]
    public async Task Blocks_hostname_resolving_to_loopback()
    {
        // Fake resolver that returns 127.0.0.1 for any hostname
        var resolver = new FakeDnsResolver(IPAddress.Loopback);
        var result = await UrlValidator.ValidateWithDnsAsync("https://evil.com", resolver);

        Assert.False(result.IsValid);
        Assert.Contains("private", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Blocks_hostname_resolving_to_private_ip()
    {
        var resolver = new FakeDnsResolver(IPAddress.Parse("10.0.0.1"));
        var result = await UrlValidator.ValidateWithDnsAsync("https://sneaky.com", resolver);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Blocks_hostname_resolving_to_link_local()
    {
        var resolver = new FakeDnsResolver(IPAddress.Parse("169.254.169.254"));
        var result = await UrlValidator.ValidateWithDnsAsync("https://metadata.internal", resolver);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Allows_hostname_resolving_to_public_ip()
    {
        var resolver = new FakeDnsResolver(IPAddress.Parse("93.184.216.34"));
        var result = await UrlValidator.ValidateWithDnsAsync("https://example.com", resolver);

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// Test double for DNS resolution.
    /// </summary>
    private sealed class FakeDnsResolver(IPAddress resolvedAddress) : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken ct = default)
            => Task.FromResult(new[] { resolvedAddress });
    }
}
