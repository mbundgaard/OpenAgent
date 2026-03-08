using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenAgent.Tests;

/// <summary>
/// Verifies API key authentication rejects unauthenticated requests
/// and allows authenticated ones.
/// </summary>
public class ApiKeyAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidApiKey = "dev-api-key-change-me";
    private readonly WebApplicationFactory<Program> _factory;

    public ApiKeyAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithInvalidApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithValidApiKey_Returns200()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        var response = await client.GetAsync("/api/conversations");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task HealthEndpoint_WithoutApiKey_Returns200()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }
}
