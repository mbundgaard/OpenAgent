using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Channel provider that connects the agent to Telnyx voice calls via TeXML webhooks.
/// Constructs and owns the message handler and signature verifier instances so the
/// webhook endpoint can resolve them without going through the DI container.
/// </summary>
public sealed class TelnyxChannelProvider : IChannelProvider
{
    private readonly TelnyxOptions _options;
    private readonly string _connectionId;
    private readonly IConnectionStore _connectionStore;
    private readonly ILogger<TelnyxChannelProvider> _logger;

    /// <summary>Strongly-typed configuration for this connection. Exposed for tests that read back factory-parsed values.</summary>
    public TelnyxOptions Options => _options;

    /// <summary>Identifier of the owning connection row.</summary>
    public string ConnectionId => _connectionId;

    /// <summary>Message handler used by the webhook endpoint to process turns.</summary>
    public TelnyxMessageHandler Handler { get; }

    /// <summary>Signature verifier used by the webhook endpoint to validate requests.</summary>
    public TelnyxSignatureVerifier SignatureVerifier { get; }

    /// <summary>The webhook ID embedded in the public URL path. Auto-generated on first start.</summary>
    public string? WebhookId => _options.WebhookId;

    /// <summary>Creates a provider for the given connection. The factory is the only intended caller.</summary>
    public TelnyxChannelProvider(
        TelnyxOptions options,
        string connectionId,
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentConfig agentConfig,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _connectionId = connectionId;
        _connectionStore = connectionStore;
        _logger = loggerFactory.CreateLogger<TelnyxChannelProvider>();

        SignatureVerifier = new TelnyxSignatureVerifier(loggerFactory.CreateLogger<TelnyxSignatureVerifier>());
        Handler = new TelnyxMessageHandler(
            options,
            connectionId,
            store,
            textProviderResolver,
            agentConfig,
            loggerFactory.CreateLogger<TelnyxMessageHandler>());
    }

    /// <summary>
    /// Starts the Telnyx channel. On first start, auto-generates and persists a WebhookId.
    /// </summary>
    public Task StartAsync(CancellationToken ct)
    {
        // Generate webhookId if not yet persisted — first start for this connection
        if (string.IsNullOrWhiteSpace(_options.WebhookId))
        {
            _options.WebhookId = Guid.NewGuid().ToString("N")[..12];

            // Persist webhookId back to connection config so it survives restarts
            var connection = _connectionStore.Load(_connectionId);
            if (connection is not null)
            {
                var configDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(connection.Config) ?? [];
                configDict["webhookId"] = _options.WebhookId;
                connection.Config = System.Text.Json.JsonSerializer.SerializeToElement(configDict);
                _connectionStore.Save(connection);
                _logger.LogInformation("Telnyx: generated webhookId {WebhookId} for connection {ConnectionId}", _options.WebhookId, _connectionId);
            }
        }

        _logger.LogInformation(
            "Telnyx [{ConnectionId}] started (phoneNumber={PhoneNumber}, webhookId={WebhookId}, allowedCount={AllowedCount})",
            _connectionId,
            _options.PhoneNumber ?? "<unset>",
            _options.WebhookId,
            _options.AllowedNumbers.Count);

        return Task.CompletedTask;
    }

    /// <summary>Stops the Telnyx channel. No-op for TeXML mode — webhooks are stateless.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Telnyx [{ConnectionId}] stopped", _connectionId);
        return Task.CompletedTask;
    }
}
