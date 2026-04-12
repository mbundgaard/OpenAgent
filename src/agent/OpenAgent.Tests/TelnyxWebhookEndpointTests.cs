using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using OpenAgent.Tests.Fakes;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Xunit;

namespace OpenAgent.Tests;

/// <summary>
/// Integration tests for the Telnyx webhook endpoint HTTP behavior.
/// Validates provider-lookup routing and TeXML response shape.
/// </summary>
public class TelnyxWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    // Generated once per test-class load — used by the signature-rejection test.
    private static readonly string TestPublicKeyPem;

    static TelnyxWebhookEndpointTests()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var pub = (Ed25519PublicKeyParameters)pair.Public;

        using var sw = new StringWriter();
        var pemWriter = new PemWriter(sw);
        pemWriter.WriteObject(pub);
        pemWriter.Writer.Flush();
        TestPublicKeyPem = sw.ToString();
    }

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

    [Fact]
    public async Task Speech_webhook_for_known_webhookId_returns_TeXML_with_agent_reply()
    {
        // Seed a factory with a provider that returns a known reply.
        // Must replace Func<string, ILlmTextProvider> — TelnyxMessageHandler resolves by key.
        const string agentReply = "Forty-two.";
        var fakeProvider = new FakeTelnyxTextProvider(agentReply);
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(ILlmTextProvider));
                services.RemoveAll(typeof(Func<string, ILlmTextProvider>));
                services.AddSingleton<ILlmTextProvider>(fakeProvider);
                services.AddSingleton<Func<string, ILlmTextProvider>>(_ => _ => fakeProvider);
            });
        });

        var store = factory.Services.GetRequiredService<IConnectionStore>();
        var connectionManager = factory.Services.GetRequiredService<IConnectionManager>();

        var configJson = JsonSerializer.SerializeToElement(new
        {
            apiKey = "KEY",
            phoneNumber = "+4598765432",
            baseUrl = "http://localhost",
            webhookId = "speech-test-webhook-id",
            allowedNumbers = "",
        });

        var connection = new Connection
        {
            Id = "telnyx-speech-conn",
            Name = "Telnyx Speech Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = Guid.NewGuid().ToString(),
            Config = configJson,
        };

        store.Save(connection);
        await connectionManager.StartConnectionAsync(connection.Id, default);

        var client = factory.CreateClient();

        // Seed the call via /voice first so the conversation exists
        var voiceForm = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("To", "+4598765432"),
            new KeyValuePair<string, string>("CallSid", "call-speech-test"),
        ]);
        await client.PostAsync("/api/webhook/telnyx/speech-test-webhook-id/voice", voiceForm);

        // Now send a speech result
        var speechForm = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("CallSid", "call-speech-test"),
            new KeyValuePair<string, string>("SpeechResult", "What is the answer?"),
        ]);

        var resp = await client.PostAsync("/api/webhook/telnyx/speech-test-webhook-id/speech", speechForm);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/xml", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Say>Forty-two.</Say>", body);
        Assert.Contains("<Gather", body);
    }

    [Fact]
    public async Task Status_webhook_with_completed_returns_200_ok()
    {
        var store = _factory.Services.GetRequiredService<IConnectionStore>();
        var connectionManager = _factory.Services.GetRequiredService<IConnectionManager>();

        var configJson = JsonSerializer.SerializeToElement(new
        {
            apiKey = "KEY",
            phoneNumber = "+4598765432",
            baseUrl = "http://localhost",
            webhookId = "status-test-webhook-id",
            allowedNumbers = "",
        });

        var connection = new Connection
        {
            Id = "telnyx-status-conn",
            Name = "Telnyx Status Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = Guid.NewGuid().ToString(),
            Config = configJson,
        };

        store.Save(connection);
        await connectionManager.StartConnectionAsync(connection.Id, default);

        var client = _factory.CreateClient();
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("CallSid", "call-status-test"),
            new KeyValuePair<string, string>("CallStatus", "completed"),
        ]);

        var resp = await client.PostAsync("/api/webhook/telnyx/status-test-webhook-id/status", form);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Voice_webhook_with_configured_key_rejects_missing_signature_as_404()
    {
        // Seed a connection with WebhookPublicKey set — dev-mode bypass is OFF
        var store = _factory.Services.GetRequiredService<IConnectionStore>();
        var connectionManager = _factory.Services.GetRequiredService<IConnectionManager>();

        var configJson = JsonSerializer.SerializeToElement(new
        {
            apiKey = "KEY",
            phoneNumber = "+4598765432",
            baseUrl = "http://localhost",
            webhookId = "sig-test-webhook-id",
            allowedNumbers = "",
            webhookPublicKey = TestPublicKeyPem,
        });

        var connection = new Connection
        {
            Id = "telnyx-sig-conn",
            Name = "Telnyx Sig Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = Guid.NewGuid().ToString(),
            Config = configJson,
        };

        store.Save(connection);
        await connectionManager.StartConnectionAsync(connection.Id, default);

        var client = _factory.CreateClient();
        // POST without Telnyx-Signature-ed25519 / Telnyx-Timestamp headers — signature verification must fail
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("From", "+4512345678"),
            new KeyValuePair<string, string>("To", "+4598765432"),
            new KeyValuePair<string, string>("CallSid", "call-sig-test"),
        ]);

        var resp = await client.PostAsync("/api/webhook/telnyx/sig-test-webhook-id/voice", form);

        // Must return 404 — same as unknown webhookId — to avoid leaking endpoint existence to scanners
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
