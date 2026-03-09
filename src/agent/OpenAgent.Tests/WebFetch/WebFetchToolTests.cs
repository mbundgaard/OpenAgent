using System.Net;
using System.Text.Json;
using OpenAgent.Tools.WebFetch;

namespace OpenAgent.Tests.WebFetch;

public class WebFetchToolTests
{
    [Fact]
    public void Definition_has_correct_name()
    {
        var tool = CreateTool();
        Assert.Equal("web_fetch", tool.Definition.Name);
    }

    [Fact]
    public void Definition_has_url_parameter()
    {
        var tool = CreateTool();
        var json = JsonSerializer.Serialize(tool.Definition.Parameters);
        Assert.Contains("url", json);
    }

    [Fact]
    public async Task Fetches_url_and_returns_markdown_content()
    {
        var html = "<html><head><title>Test Page</title></head><body><article><h1>Hello</h1><p>World paragraph content here.</p></article></body></html>";
        var tool = CreateTool(html);

        var result = await tool.ExecuteAsync("""{"url": "https://example.com"}""");
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("https://example.com", root.GetProperty("url").GetString());
        Assert.Contains("Hello", root.GetProperty("content").GetString());
        Assert.Equal("http", root.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Returns_error_for_invalid_url()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"url": "ftp://evil.com"}""");
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.True(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Returns_error_for_private_ip()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"url": "http://192.168.1.1"}""");
        var doc = JsonDocument.Parse(result);

        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Returns_error_on_http_failure()
    {
        var tool = CreateTool(statusCode: HttpStatusCode.InternalServerError);

        var result = await tool.ExecuteAsync("""{"url": "https://example.com"}""");
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(500, root.GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public async Task Respects_max_chars_parameter()
    {
        var longContent = new string('x', 5000);
        var html = $"<html><head><title>Long</title></head><body><article><p>{longContent}</p></article></body></html>";
        var tool = CreateTool(html);

        var result = await tool.ExecuteAsync("""{"url": "https://example.com", "maxChars": 100}""");
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("charCount").GetInt32() <= 100);
    }

    [Fact]
    public async Task Returns_error_when_url_missing()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{}""");
        var doc = JsonDocument.Parse(result);

        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    // --- Finding #1: DNS-based SSRF in tool ---

    [Fact]
    public async Task Blocks_fetch_when_hostname_resolves_to_private_ip()
    {
        var resolver = new FakeDnsResolver(IPAddress.Parse("10.0.0.1"));
        var tool = CreateTool(dnsResolver: resolver);

        var result = await tool.ExecuteAsync("""{"url": "https://sneaky.com"}""");
        var doc = JsonDocument.Parse(result);

        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("private", doc.RootElement.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    // --- Finding #2: Malformed JSON arguments ---

    [Fact]
    public async Task Returns_error_for_malformed_json()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("not valid json{{{");
        var doc = JsonDocument.Parse(result);

        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // --- Finding #3: Negative maxChars ---

    [Fact]
    public async Task Clamps_negative_max_chars_to_safe_value()
    {
        var html = "<html><head><title>Test</title></head><body><article><p>Some content.</p></article></body></html>";
        var tool = CreateTool(html);

        // Should not throw — negative maxChars should be clamped
        var result = await tool.ExecuteAsync("""{"url": "https://example.com", "maxChars": -5}""");
        var doc = JsonDocument.Parse(result);

        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("charCount").GetInt32() >= 0);
    }

    [Fact]
    public async Task Clamps_zero_max_chars_to_safe_value()
    {
        var html = "<html><head><title>Test</title></head><body><article><p>Some content.</p></article></body></html>";
        var tool = CreateTool(html);

        var result = await tool.ExecuteAsync("""{"url": "https://example.com", "maxChars": 0}""");
        var doc = JsonDocument.Parse(result);

        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    // --- Helpers ---

    private static WebFetchTool CreateTool(string responseBody = "", HttpStatusCode statusCode = HttpStatusCode.OK, IDnsResolver? dnsResolver = null)
    {
        var handler = new FakeHttpHandler(responseBody, statusCode);
        var client = new HttpClient(handler);
        // Default to a resolver that returns a public IP (passes SSRF check)
        dnsResolver ??= new FakeDnsResolver(IPAddress.Parse("93.184.216.34"));
        return new WebFetchTool(client, dnsResolver);
    }

    private sealed class FakeHttpHandler(string responseBody, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "text/html")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeDnsResolver(IPAddress resolvedAddress) : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken ct = default)
            => Task.FromResult(new[] { resolvedAddress });
    }
}
