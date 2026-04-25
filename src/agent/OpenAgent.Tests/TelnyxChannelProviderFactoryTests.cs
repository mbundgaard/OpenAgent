using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Channel.Telnyx;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using Xunit;

namespace OpenAgent.Tests;

/// <summary>Minimal IHttpClientFactory for unit tests — TelnyxChannelProvider eagerly creates a Call Control HttpClient in its ctor.</summary>
file sealed class StubHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

/// <summary>
/// Unit tests for <see cref="TelnyxChannelProviderFactory"/> covering type metadata, config-field
/// surface, and the JsonElement → TelnyxOptions parse path used when materialising a provider.
/// </summary>
public class TelnyxChannelProviderFactoryTests
{
    [Fact]
    public void Type_IsTelnyx()
    {
        var factory = NewFactory();
        Assert.Equal("telnyx", factory.Type);
    }

    [Fact]
    public void ConfigFields_IncludesAllRequired()
    {
        var factory = NewFactory();
        var keys = factory.ConfigFields.Select(f => f.Key).ToList();
        Assert.Contains("apiKey", keys);
        Assert.Contains("phoneNumber", keys);
        Assert.Contains("baseUrl", keys);
        Assert.Contains("callControlAppId", keys);
        Assert.Contains("webhookPublicKey", keys);
        Assert.Contains("allowedNumbers", keys);
    }

    [Fact]
    public void Create_DeserializesOptions()
    {
        var json = """
        {"apiKey":"K","phoneNumber":"+45","baseUrl":"https://x","callControlAppId":"app","webhookPublicKey":"PEM","allowedNumbers":"+4520,+4530","webhookId":"abc"}
        """;
        var conn = new Connection
        {
            Id = "c",
            Name = "n",
            Type = "telnyx",
            Enabled = true,
            // Connection.ConversationId is required at model level. Telnyx derives a per-call
            // conversation from the caller E.164 at runtime, so this property is unused.
            ConversationId = "unused",
            Config = JsonSerializer.Deserialize<JsonElement>(json)
        };
        var factory = NewFactory();
        var provider = (TelnyxChannelProvider)factory.Create(conn);
        Assert.Equal("K", provider.Options.ApiKey);
        Assert.Equal("+45", provider.Options.PhoneNumber);
        Assert.Equal(["+4520", "+4530"], provider.Options.AllowedNumbers);
        Assert.Equal("abc", provider.Options.WebhookId);
    }

    private static TelnyxChannelProviderFactory NewFactory() =>
        new TelnyxChannelProviderFactory(
            store: null!,
            connectionStore: null!,
            voiceProviderResolver: _ => null!,
            agentConfig: null!,
            environment: new AgentEnvironment { DataPath = Path.GetTempPath() },
            bridgeRegistry: new TelnyxBridgeRegistry(),
            httpClientFactory: new StubHttpClientFactory(),
            loggerFactory: NullLoggerFactory.Instance);
}
