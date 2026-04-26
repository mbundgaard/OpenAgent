using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Connections;
using OpenAgent.Models.Providers;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Creates <see cref="TelnyxChannelProvider"/> instances from a connection's stored config blob.
/// Exposes the field schema the settings UI uses to render the dynamic form.
/// </summary>
public sealed class TelnyxChannelProviderFactory : IChannelProviderFactory
{
    private readonly IConversationStore _store;
    private readonly IConnectionStore _connectionStore;
    private readonly Func<string, ILlmVoiceProvider> _voiceProviderResolver;
    private readonly AgentConfig _agentConfig;
    private readonly AgentEnvironment _environment;
    private readonly TelnyxBridgeRegistry _bridgeRegistry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>The connection type this factory handles.</summary>
    public string Type => "telnyx";

    /// <summary>Human-readable name shown in the UI.</summary>
    public string DisplayName => "Telnyx";

    /// <summary>Configuration fields rendered by the settings UI for this channel type.</summary>
    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey",           Label = "Telnyx API Key",                                              Type = "Secret", Required = true },
        new() { Key = "phoneNumber",      Label = "Phone Number (E.164)",                                        Type = "String", Required = true },
        new() { Key = "baseUrl",          Label = "Public Base URL (https)",                                     Type = "String", Required = true },
        new() { Key = "callControlAppId", Label = "Call Control Connection ID",                                  Type = "String", Required = true },
        new() { Key = "webhookPublicKey", Label = "Webhook Public Key (PEM, leave empty for dev)",               Type = "Secret" },
        new() { Key = "allowedNumbers",   Label = "Allowed Caller Numbers (comma-separated, empty = allow all)", Type = "String" },
    ];

    /// <summary>Telnyx has no post-creation setup step — the user just enters API credentials.</summary>
    public ChannelSetupStep? SetupStep => null;

    public TelnyxChannelProviderFactory(
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmVoiceProvider> voiceProviderResolver,
        AgentConfig agentConfig,
        AgentEnvironment environment,
        TelnyxBridgeRegistry bridgeRegistry,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _connectionStore = connectionStore;
        _voiceProviderResolver = voiceProviderResolver;
        _agentConfig = agentConfig;
        _environment = environment;
        _bridgeRegistry = bridgeRegistry;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Deserializes the connection's config blob and constructs a provider for it.</summary>
    public IChannelProvider Create(Connection connection)
    {
        var options = ParseOptions(connection.Config);
        return new TelnyxChannelProvider(
            options,
            connection.Id,
            _store,
            _connectionStore,
            _voiceProviderResolver,
            _agentConfig,
            _environment,
            _bridgeRegistry,
            _httpClientFactory,
            _loggerFactory);
    }

    // The dynamic settings form posts allowedNumbers as a comma-separated string; the JSON
    // file may also hold it as an array after a manual edit. Accept both formats so user
    // edits are durable across UI round-trips.
    private static TelnyxOptions ParseOptions(JsonElement config)
    {
        var opts = new TelnyxOptions();
        if (config.ValueKind != JsonValueKind.Object) return opts;
        if (config.TryGetProperty("apiKey", out var p) && p.ValueKind == JsonValueKind.String) opts.ApiKey = p.GetString();
        if (config.TryGetProperty("phoneNumber", out p) && p.ValueKind == JsonValueKind.String) opts.PhoneNumber = p.GetString();
        if (config.TryGetProperty("baseUrl", out p) && p.ValueKind == JsonValueKind.String) opts.BaseUrl = p.GetString();
        if (config.TryGetProperty("callControlAppId", out p) && p.ValueKind == JsonValueKind.String) opts.CallControlAppId = p.GetString();
        if (config.TryGetProperty("webhookPublicKey", out p) && p.ValueKind == JsonValueKind.String) opts.WebhookPublicKey = p.GetString();
        if (config.TryGetProperty("webhookId", out p) && p.ValueKind == JsonValueKind.String) opts.WebhookId = p.GetString();
        if (config.TryGetProperty("allowedNumbers", out p))
        {
            opts.AllowedNumbers = p.ValueKind switch
            {
                JsonValueKind.String when p.GetString() is { Length: > 0 } s =>
                    s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                JsonValueKind.Array => p.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => s.Length > 0)
                    .ToList(),
                _ => [],
            };
        }
        return opts;
    }
}
