using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Connections;
using OpenAgent.Models.Providers;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Creates <see cref="TelnyxChannelProvider"/> instances from connection configuration.
/// </summary>
public sealed class TelnyxChannelProviderFactory : IChannelProviderFactory
{
    private readonly IConversationStore _store;
    private readonly IConnectionStore _connectionStore;
    private readonly Func<string, ILlmTextProvider> _textProviderResolver;
    private readonly AgentConfig _agentConfig;
    private readonly ILoggerFactory _loggerFactory;

    /// <inheritdoc/>
    public string Type => "telnyx";

    /// <inheritdoc/>
    public string DisplayName => "Telnyx";

    /// <inheritdoc/>
    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey",           Label = "API Key",                        Type = "Secret", Required = true },
        new() { Key = "phoneNumber",      Label = "Phone Number (E.164)",           Type = "String", Required = true },
        new() { Key = "baseUrl",          Label = "Public Base URL",                Type = "String", Required = true },
        new() { Key = "webhookPublicKey", Label = "Webhook Public Key (PEM)",       Type = "Secret" },
        new() { Key = "webhookSecret",    Label = "Webhook Signing Secret",         Type = "Secret" },
        new() { Key = "allowedNumbers",   Label = "Allowed Caller Numbers (comma-separated, empty = allow all)", Type = "String" },
    ];

    /// <inheritdoc/>
    public ChannelSetupStep? SetupStep => null;

    public TelnyxChannelProviderFactory(
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentConfig agentConfig,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _connectionStore = connectionStore;
        _textProviderResolver = textProviderResolver;
        _agentConfig = agentConfig;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Deserializes the connection's config into TelnyxOptions and creates the provider.</summary>
    public IChannelProvider Create(Connection connection)
    {
        // Parse config manually — the dynamic form sends comma-separated strings
        // for list fields. Mirrors TelegramChannelProviderFactory.Create.
        var options = new TelnyxOptions();

        if (connection.Config.ValueKind == JsonValueKind.Object)
        {
            if (connection.Config.TryGetProperty("apiKey", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                options.ApiKey = keyEl.GetString();

            if (connection.Config.TryGetProperty("phoneNumber", out var phoneEl) && phoneEl.ValueKind == JsonValueKind.String)
                options.PhoneNumber = phoneEl.GetString();

            if (connection.Config.TryGetProperty("webhookSecret", out var secretEl) && secretEl.ValueKind == JsonValueKind.String)
                options.WebhookSecret = secretEl.GetString();

            if (connection.Config.TryGetProperty("baseUrl", out var baseUrlEl) && baseUrlEl.ValueKind == JsonValueKind.String)
                options.BaseUrl = baseUrlEl.GetString();

            if (connection.Config.TryGetProperty("webhookPublicKey", out var keyPemEl) && keyPemEl.ValueKind == JsonValueKind.String)
                options.WebhookPublicKey = keyPemEl.GetString();

            if (connection.Config.TryGetProperty("webhookId", out var webhookIdEl) && webhookIdEl.ValueKind == JsonValueKind.String)
                options.WebhookId = webhookIdEl.GetString();

            if (connection.Config.TryGetProperty("allowedNumbers", out var allowedEl))
            {
                if (allowedEl.ValueKind == JsonValueKind.Array)
                    options.AllowedNumbers = allowedEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .Where(s => s.Length > 0)
                        .ToList();
                else if (allowedEl.ValueKind == JsonValueKind.String)
                {
                    var raw = allowedEl.GetString() ?? "";
                    options.AllowedNumbers = raw.Length > 0
                        ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                        : [];
                }
            }
        }

        return new TelnyxChannelProvider(
            options,
            connection.Id,
            _store,
            _connectionStore,
            _textProviderResolver,
            _agentConfig,
            _loggerFactory);
    }
}
