using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using OpenAgent.Models.Connections;
using Xunit;

namespace OpenAgent.Tests;

public class TelnyxChannelProviderFactoryTests
{
    [Fact]
    public void Factory_exposes_expected_metadata()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);

        Assert.Equal("telnyx", factory.Type);
        Assert.Equal("Telnyx", factory.DisplayName);
        Assert.Null(factory.SetupStep);
    }

    [Fact]
    public void ConfigFields_declares_expected_keys()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);

        var keys = factory.ConfigFields.Select(f => f.Key).ToArray();

        Assert.Contains("apiKey", keys);
        Assert.Contains("phoneNumber", keys);
        Assert.Contains("webhookSecret", keys);
        Assert.Contains("allowedNumbers", keys);
    }

    [Fact]
    public void ApiKey_and_webhookSecret_are_secret_fields()
    {
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);

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
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
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
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
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
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
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
        var factory = new TelnyxChannelProviderFactory(NullLoggerFactory.Instance);
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
}
