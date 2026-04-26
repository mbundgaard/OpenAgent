using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;

namespace OpenAgent.Channel.Telnyx;

/// <summary>
/// Owns the runtime state for one Telnyx connection: the Call Control client, the signature
/// verifier, the allow-list, the procedural thinking clip, and the pending-bridge dictionary.
/// Active bridges are tracked in the global <see cref="TelnyxBridgeRegistry"/> instead so the
/// EndCallTool (an app-singleton) can find them without going through this provider.
/// </summary>
public sealed class TelnyxChannelProvider : IChannelProvider
{
    private readonly TelnyxOptions _options;
    private readonly string _connectionId;
    private readonly IConnectionStore _connectionStore;
    private readonly IConversationStore _store;
    private readonly Func<string, ILlmVoiceProvider> _voiceProviderResolver;
    private readonly AgentConfig _agentConfig;
    private readonly AgentEnvironment _environment;
    private readonly TelnyxBridgeRegistry _bridgeRegistry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TelnyxChannelProvider> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingBridge> _pending = new();

    public TelnyxOptions Options => _options;
    public string ConnectionId => _connectionId;
    public TelnyxBridgeRegistry BridgeRegistry => _bridgeRegistry;
    public TelnyxSignatureVerifier SignatureVerifier { get; }
    public TelnyxCallControlClient CallControlClient { get; }
    public byte[] ThinkingClip { get; private set; } = [];
    public AgentConfig AgentConfig => _agentConfig;
    public AgentEnvironment Environment => _environment;
    public IConversationStore ConversationStore => _store;
    public Func<string, ILlmVoiceProvider> VoiceProviderResolver => _voiceProviderResolver;
    public ILoggerFactory LoggerFactory => _loggerFactory;

    public TelnyxChannelProvider(
        TelnyxOptions options,
        string connectionId,
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmVoiceProvider> voiceProviderResolver,
        AgentConfig agentConfig,
        AgentEnvironment environment,
        TelnyxBridgeRegistry bridgeRegistry,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _connectionId = connectionId;
        _store = store;
        _connectionStore = connectionStore;
        _voiceProviderResolver = voiceProviderResolver;
        _agentConfig = agentConfig;
        _environment = environment;
        _bridgeRegistry = bridgeRegistry;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TelnyxChannelProvider>();

        SignatureVerifier = new TelnyxSignatureVerifier(loggerFactory.CreateLogger<TelnyxSignatureVerifier>());
        CallControlClient = new TelnyxCallControlClient(
            httpClientFactory.CreateClient(nameof(TelnyxCallControlClient)),
            options.ApiKey ?? throw new InvalidOperationException("Telnyx ApiKey is required."),
            loggerFactory.CreateLogger<TelnyxCallControlClient>());
    }

    /// <summary>
    /// Validates required fields, generates and persists a webhook id on first start, and
    /// loads the thinking clip into memory. No network listening happens here — Telnyx delivers
    /// webhooks directly to the public endpoints registered by the host.
    /// </summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("Telnyx BaseUrl is required.");
        if (string.IsNullOrWhiteSpace(_options.CallControlAppId))
            throw new InvalidOperationException("Telnyx CallControlAppId is required.");

        // Generate a stable webhook id on first start so the URL the user pastes into the
        // Telnyx Developer Hub stays valid across restarts. Persist it back to connections.json.
        if (string.IsNullOrWhiteSpace(_options.WebhookId))
        {
            _options.WebhookId = Guid.NewGuid().ToString("N")[..12];
            var connection = _connectionStore.Load(_connectionId);
            if (connection is not null)
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(connection.Config) ?? [];
                dict["webhookId"] = _options.WebhookId;
                connection.Config = JsonSerializer.SerializeToElement(dict);
                _connectionStore.Save(connection);
                _logger.LogInformation("Telnyx: generated webhookId {WebhookId} for connection {ConnectionId}",
                    _options.WebhookId, _connectionId);
            }
        }

        ThinkingClip = LoadThinkingClip();

        _logger.LogInformation(
            "Telnyx [{ConnectionId}] started: phone={Phone}, webhookId={WebhookId}, allow={Allow}",
            _connectionId, _options.PhoneNumber, _options.WebhookId, _options.AllowedNumbers.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public bool TryRegisterPending(string callControlId, PendingBridge pending) =>
        _pending.TryAdd(callControlId, pending);

    public bool TryDequeuePending(string callControlId, out PendingBridge? pending)
    {
        var ok = _pending.TryRemove(callControlId, out var p);
        pending = p;
        return ok;
    }

    private byte[] LoadThinkingClip()
    {
        if (string.IsNullOrWhiteSpace(_options.ThinkingClipPath))
            return ThinkingClipFactory.Generate();

        var fullPath = Path.Combine(_environment.DataPath, _options.ThinkingClipPath);
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Telnyx ThinkingClipPath {Path} missing — falling back to procedural default", fullPath);
            return ThinkingClipFactory.Generate();
        }

        var bytes = File.ReadAllBytes(fullPath);
        // µ-law 8 kHz, 20 ms = 160 bytes per frame; require multiple of frame size for clean looping.
        if (bytes.Length % 160 != 0)
        {
            _logger.LogWarning("Telnyx ThinkingClipPath {Path} is not a multiple of 160 bytes — falling back to procedural default", fullPath);
            return ThinkingClipFactory.Generate();
        }
        return bytes;
    }
}

public sealed record PendingBridge(
    string CallControlId,
    string ConversationId,
    string VoiceProviderKey,
    CancellationTokenSource Cts);
