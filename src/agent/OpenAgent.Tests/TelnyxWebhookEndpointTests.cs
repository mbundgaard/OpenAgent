using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using Xunit;

namespace OpenAgent.Tests;

/// <summary>
/// Integration tests for the Telnyx call lifecycle webhook endpoint.
/// Verifies route presence (404 for unknown webhookId) and connection_id validation
/// (401 for mismatched callControlAppId).
/// </summary>
public class TelnyxWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string TestConnectionId = "test-telnyx";
    private const string TestWebhookId = "abcdef123456";
    private const string TestCallControlAppId = "telnyx-cc-app";

    private readonly WebApplicationFactory<Program> _factory;

    public TelnyxWebhookEndpointTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _factory = factory;
    }

    [Fact]
    public async Task UnknownWebhookId_Returns404()
    {
        using var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new { data = new { event_type = "call.initiated" } });
        var res = await client.PostAsync("/api/webhook/telnyx/unknown1234/call",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task CallInitiated_BadConnectionId_Returns401()
    {
        using var client = _factory.CreateClient();
        await SetupRunningConnectionAsync();

        var body = JsonSerializer.Serialize(new
        {
            data = new
            {
                event_type = "call.initiated",
                payload = new { call_control_id = "call-1", from = "+4520", to = "+4535150636", connection_id = "WRONG" }
            }
        });
        var res = await client.PostAsync($"/api/webhook/telnyx/{TestWebhookId}/call",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>
    /// Seeds a Telnyx connection into the running host's IConnectionStore and starts it via
    /// IConnectionManager. WebhookPublicKey is left blank so the verifier takes the dev-skip
    /// path — incoming requests don't need to be signed for the route handler to run.
    /// </summary>
    private async Task SetupRunningConnectionAsync()
    {
        var store = _factory.Services.GetRequiredService<IConnectionStore>();
        var manager = _factory.Services.GetRequiredService<IConnectionManager>();

        var config = JsonSerializer.SerializeToElement(new
        {
            apiKey = "test-key",
            phoneNumber = "+4535150636",
            baseUrl = "https://example.com",
            callControlAppId = TestCallControlAppId,
            webhookId = TestWebhookId,
            // webhookPublicKey deliberately omitted -> dev-skip path in TelnyxSignatureVerifier
        });

        store.Save(new Connection
        {
            Id = TestConnectionId,
            Name = "Test Telnyx",
            Type = "telnyx",
            Enabled = true,
            ConversationId = "unused",  // Telnyx derives per-call conversation from caller E.164
            Config = config,
        });

        await manager.StartConnectionAsync(TestConnectionId, default);
    }
}
