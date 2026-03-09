using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Tools.WebFetch;

/// <summary>
/// Fetches a URL and extracts readable content as markdown.
/// </summary>
public sealed class WebFetchTool(HttpClient httpClient, IDnsResolver dnsResolver) : ITool
{
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const int DefaultMaxChars = 50_000;
    private const int MaxResponseBytes = 2_000_000;

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "web_fetch",
        Description = "Fetch a URL and extract readable content as markdown",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "URL to fetch (http or https)" },
                maxChars = new { type = "integer", description = "Maximum characters to return (default 50000)" }
            },
            required = new[] { "url" }
        }
    };

    public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        JsonElement args;
        try
        {
            args = JsonDocument.Parse(arguments).RootElement;
        }
        catch (JsonException)
        {
            return Error("Invalid JSON arguments");
        }

        // Extract url parameter
        if (!args.TryGetProperty("url", out var urlEl) || urlEl.GetString() is not { } url)
            return Error("url is required");

        // Validate URL (scheme check, SSRF protection with DNS resolution)
        var validation = await UrlValidator.ValidateWithDnsAsync(url, dnsResolver, ct);
        if (!validation.IsValid)
            return Error(validation.Error!, url);

        // Parse optional maxChars, clamped to safe range
        var maxChars = args.TryGetProperty("maxChars", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number
            ? Math.Clamp(maxEl.GetInt32(), 1, DefaultMaxChars)
            : DefaultMaxChars;

        // Fetch the page
        HttpResponseMessage response;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, validation.Uri);
            request.Headers.UserAgent.ParseAdd(DefaultUserAgent);
            response = await httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Error(ex.Message, url);
        }

        if (!response.IsSuccessStatusCode)
            return Error($"HTTP {(int)response.StatusCode}", url, (int)response.StatusCode);

        // Read response body with size cap
        var html = await response.Content.ReadAsStringAsync(ct);
        if (html.Length > MaxResponseBytes)
            html = html[..MaxResponseBytes];

        // Extract content
        var extraction = ContentExtractor.Extract(html, url, maxChars);

        return JsonSerializer.Serialize(new
        {
            success = true,
            url,
            title = extraction.Title,
            content = extraction.Content,
            charCount = extraction.CharCount,
            truncated = extraction.Truncated,
            source = "http"
        });
    }

    private static string Error(string error, string? url = null, int? httpStatus = null)
    {
        if (httpStatus.HasValue)
            return JsonSerializer.Serialize(new { success = false, url, error, httpStatus = httpStatus.Value });
        if (url is not null)
            return JsonSerializer.Serialize(new { success = false, url, error });
        return JsonSerializer.Serialize(new { success = false, error });
    }
}
