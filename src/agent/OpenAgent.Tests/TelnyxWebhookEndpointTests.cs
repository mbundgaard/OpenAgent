using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using OpenAgent.Tests.Fakes;
using Xunit;

namespace OpenAgent.Tests;

/// <summary>
/// Integration tests for the Telnyx webhook endpoint HTTP behavior.
/// Validates provider-lookup routing and TeXML response shape.
/// </summary>
public class TelnyxWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TelnyxWebhookEndpointTests(WebApplicationFactory<Program> factory)
    {
        TestSetup.EnsureConfigSeeded();
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace ILlmTextProvider with a fake — voice handler returns greeting without calling the provider
                services.RemoveAll(typeof(ILlmTextProvider));
                services.AddSingleton<ILlmTextProvider>(new FakeTelnyxTextProvider("ignored-in-voice-handler"));
            });
        });
    }

    [Fact]
    public async Task Voice_webhook_for_unknown_webhookId_returns_404()
    {
        var client = _factory.CreateClient();
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("To", "+4598765432"),
            new KeyValuePair<string, string>("CallSid", "call-x"),
        ]);

        var resp = await client.PostAsync("/api/webhook/telnyx/unknown/voice", form);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Voice_webhook_is_anonymous_no_api_key_needed()
    {
        // Should return 404 (no connection), not 401 (auth required)
        var client = _factory.CreateClient();
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("To", "+4598765432"),
            new KeyValuePair<string, string>("CallSid", "call-x"),
        ]);

        var resp = await client.PostAsync("/api/webhook/telnyx/unknown/voice", form);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Voice_webhook_for_known_webhookId_returns_TeXML()
    {
        // Seed a Telnyx connection with a fixed webhookId and no public key (dev-mode bypass)
        var store = _factory.Services.GetRequiredService<IConnectionStore>();
        var connectionManager = _factory.Services.GetRequiredService<IConnectionManager>();

        var configJson = JsonSerializer.SerializeToElement(new
        {
            apiKey = "KEY",
            phoneNumber = "+4598765432",
            baseUrl = "http://localhost",
            webhookId = "test-webhook-id",
            allowedNumbers = "",
            // webhookPublicKey omitted — null triggers dev-mode bypass in TelnyxSignatureVerifier
        });

        var connection = new Connection
        {
            Id = "telnyx-test-conn",
            Name = "Telnyx Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = Guid.NewGuid().ToString(),
            Config = configJson,
        };

        store.Save(connection);

        // StartConnectionAsync is safe for Telnyx — StartAsync only logs (no external API calls)
        await connectionManager.StartConnectionAsync(connection.Id, default);

        var client = _factory.CreateClient();
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("To", "+4598765432"),
            new KeyValuePair<string, string>("CallSid", "call-abc"),
        ]);

        var resp = await client.PostAsync("/api/webhook/telnyx/test-webhook-id/voice", form);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/xml", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Gather", body);
        Assert.Contains("<Say>Hi, it's OpenAgent", body);
    }
}
