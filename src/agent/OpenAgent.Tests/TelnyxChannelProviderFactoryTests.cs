using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Connections;
using OpenAgent.Tests.Fakes;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxChannelProviderFactoryTests
{
    // Helper — constructs a factory with test-friendly dependencies.
    private static TelnyxChannelProviderFactory BuildFactory(
        IConversationStore? store = null,
        IConnectionStore? connections = null,
        Func<string, ILlmTextProvider>? resolver = null,
        AgentConfig? config = null) =>
        new(
            store ?? new InMemoryConversationStore(),
            connections ?? new FakeConnectionStore(),
            resolver ?? (_ => new FakeTelnyxTextProvider("stub")),
            config ?? new AgentConfig { TextProvider = "fake", TextModel = "fake-1" },
            NullLoggerFactory.Instance);

    [Fact]
    public void Factory_exposes_expected_metadata()
    {
        var factory = BuildFactory();

        Assert.Equal("telnyx", factory.Type);
        Assert.Equal("Telnyx", factory.DisplayName);
        Assert.Null(factory.SetupStep);
    }

    [Fact]
    public void ConfigFields_declares_expected_keys()
    {
        var factory = BuildFactory();

        var keys = factory.ConfigFields.Select(f => f.Key).ToArray();

        Assert.Contains("apiKey", keys);
        Assert.Contains("phoneNumber", keys);
        Assert.Contains("webhookSecret", keys);
        Assert.Contains("allowedNumbers", keys);
    }

    [Fact]
    public void ApiKey_and_webhookSecret_are_secret_fields()
    {
        var factory = BuildFactory();

        var apiKey = factory.ConfigFields.Single(f => f.Key == "apiKey");
        var secret = factory.ConfigFields.Single(f => f.Key == "webhookSecret");

        Assert.Equal("Secret", apiKey.Type);
        Assert.True(apiKey.Required);
        Assert.Equal("Secret", secret.Type);
        Assert.False(secret.Required);

        var phoneNumber = factory.ConfigFields.Single(f => f.Key == "phoneNumber");
        Assert.True(phoneNumber.Required);
        Assert.Equal("String", phoneNumber.Type);
    }

    [Fact]
    public void Create_parses_string_config_into_options()
    {
        var factory = BuildFactory();
        var config = JsonDocument.Parse("""
            {
                "apiKey": "KEY_abc",
                "phoneNumber": "+4512345678",
                "webhookSecret": "shh",
                "baseUrl": "https://example.com",
                "webhookPublicKey": "-----BEGIN PUBLIC KEY-----\nMCo...\n-----END PUBLIC KEY-----",
                "webhookId": "abc-123",
                "allowedNumbers": "+4511111111,+4522222222"
            }
            """).RootElement;
        var connection = new Connection
        {
            Id = "conn-1",
            Name = "Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = "conv-1",
            Config = config,
        };

        var provider = (TelnyxChannelProvider)factory.Create(connection);

        Assert.Equal("KEY_abc", provider.Options.ApiKey);
        Assert.Equal("+4512345678", provider.Options.PhoneNumber);
        Assert.Equal("shh", provider.Options.WebhookSecret);
        Assert.Equal("https://example.com", provider.Options.BaseUrl);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", provider.Options.WebhookPublicKey);
        Assert.Equal("abc-123", provider.Options.WebhookId);
        Assert.Equal(new[] { "+4511111111", "+4522222222" }, provider.Options.AllowedNumbers);
        Assert.Equal("conn-1", provider.ConnectionId);
    }

    [Fact]
    public void ConfigFields_includes_baseUrl_and_webhookPublicKey()
    {
        var factory = BuildFactory();
        var keys = factory.ConfigFields.Select(f => f.Key).ToArray();

        Assert.Contains("baseUrl", keys);
        Assert.Contains("webhookPublicKey", keys);

        var baseUrl = factory.ConfigFields.Single(f => f.Key == "baseUrl");
        Assert.True(baseUrl.Required);

        var publicKey = factory.ConfigFields.Single(f => f.Key == "webhookPublicKey");
        Assert.Equal("Secret", publicKey.Type);
        Assert.False(publicKey.Required);

        Assert.DoesNotContain("webhookId", factory.ConfigFields.Select(f => f.Key).ToArray());
    }

    [Fact]
    public void Create_parses_array_allowedNumbers()
    {
        var factory = BuildFactory();
        var config = JsonDocument.Parse("""
            {
                "apiKey": "k",
                "allowedNumbers": ["+4511111111", "+4522222222"]
            }
            """).RootElement;
        var connection = new Connection
        {
            Id = "conn-2",
            Name = "Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = "conv-2",
            Config = config,
        };

        var provider = (TelnyxChannelProvider)factory.Create(connection);

        Assert.Equal(new[] { "+4511111111", "+4522222222" }, provider.Options.AllowedNumbers);
    }

    [Fact]
    public void Create_handles_missing_optional_fields()
    {
        var factory = BuildFactory();
        var config = JsonDocument.Parse("""{ "apiKey": "k" }""").RootElement;
        var connection = new Connection
        {
            Id = "conn-3",
            Name = "Test",
            Type = "telnyx",
            Enabled = true,
            ConversationId = "conv-3",
            Config = config,
        };

        var provider = (TelnyxChannelProvider)factory.Create(connection);

        Assert.Equal("k", provider.Options.ApiKey);
        Assert.Null(provider.Options.PhoneNumber);
        Assert.Null(provider.Options.WebhookSecret);
        Assert.Empty(provider.Options.AllowedNumbers);
        Assert.Null(provider.Options.BaseUrl);
        Assert.Null(provider.Options.WebhookPublicKey);
        Assert.Null(provider.Options.WebhookId);
    }

    [Fact]
    public async Task StartAsync_throws_when_BaseUrl_is_missing()
    {
        // Build a provider directly with BaseUrl absent — factory would also produce this state
        var options = new TelnyxOptions { ApiKey = "KEY", PhoneNumber = "+4512345678", BaseUrl = null };
        var provider = new TelnyxChannelProvider(
            options,
            connectionId: "conn-nobaseurl",
            store: new InMemoryConversationStore(),
            connectionStore: new FakeConnectionStore(),
            textProviderResolver: _ => new FakeTelnyxTextProvider("stub"),
            agentConfig: new AgentConfig { TextProvider = "fake", TextModel = "fake-1" },
            loggerFactory: NullLoggerFactory.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.StartAsync(default));

        Assert.Contains("BaseUrl", ex.Message);
    }
}
