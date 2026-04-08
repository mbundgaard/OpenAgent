using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Channel provider that connects the agent to Telegram via polling or webhook.
/// Creates the bot client, message handler, and manages the connection lifecycle.
/// </summary>
public sealed class TelegramChannelProvider : IChannelProvider
{
    private readonly TelegramOptions _options;
    private readonly string _connectionId;
    private readonly IConversationStore _store;
    private readonly IConnectionStore _connectionStore;
    private readonly Func<string, ILlmTextProvider> _textProviderResolver;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<TelegramChannelProvider> _logger;
    private readonly ILogger<TelegramMessageHandler> _handlerLogger;

    private TelegramBotClient? _botClient;
    private TelegramBotClientSender? _sender;
    private TelegramMessageHandler? _handler;
    private CancellationTokenSource? _pollingCts;
    private string? _webhookSecret;

    /// <summary>The underlying bot client, or null if the channel is disabled.</summary>
    public TelegramBotClient? BotClient => _botClient;

    /// <summary>The message handler that processes Telegram updates.</summary>
    public TelegramMessageHandler? Handler => _handler;

    /// <summary>The webhook secret used to validate inbound webhook requests.</summary>
    public string? WebhookSecret => _webhookSecret;

    /// <summary>The webhook ID for this connection, used in the webhook URL path.</summary>
    public string? WebhookId => _options.WebhookId;

    public TelegramChannelProvider(
        TelegramOptions options,
        string connectionId,
        IConversationStore store,
        IConnectionStore connectionStore,
        Func<string, ILlmTextProvider> textProviderResolver,
        AgentConfig agentConfig,
        ILogger<TelegramChannelProvider> logger,
        ILogger<TelegramMessageHandler> handlerLogger)
    {
        _options = options;
        _connectionId = connectionId;
        _store = store;
        _connectionStore = connectionStore;
        _textProviderResolver = textProviderResolver;
        _agentConfig = agentConfig;
        _logger = logger;
        _handlerLogger = handlerLogger;
    }

    /// <summary>
    /// Starts the Telegram channel in polling or webhook mode based on configuration.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var isWebhook = string.Equals(_options.Mode, "Webhook", StringComparison.OrdinalIgnoreCase);

        // Validate bot token
        if (string.IsNullOrEmpty(_options.BotToken))
            throw new InvalidOperationException("Telegram BotToken is required.");

        // Initialize bot client, sender, and handler
        _botClient = new TelegramBotClient(_options.BotToken);
        _sender = new TelegramBotClientSender(_botClient, _options.BotToken);
        _handler = new TelegramMessageHandler(_store, _connectionStore, _textProviderResolver, _connectionId, _agentConfig, _options, _handlerLogger);

        if (isWebhook)
        {
            if (string.IsNullOrEmpty(_options.BaseUrl))
                throw new InvalidOperationException(
                    "Telegram BaseUrl is required when Mode is 'Webhook'.");

            // Generate webhookId if not yet persisted — first start for this connection
            if (string.IsNullOrEmpty(_options.WebhookId))
            {
                _options.WebhookId = Guid.NewGuid().ToString("N");

                // Persist webhookId back to connection config so it survives restarts
                var connection = _connectionStore.Load(_connectionId);
                if (connection is not null)
                {
                    var configDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(connection.Config) ?? [];
                    configDict["webhookId"] = _options.WebhookId;
                    connection.Config = System.Text.Json.JsonSerializer.SerializeToElement(configDict);
                    _connectionStore.Save(connection);
                    _logger.LogInformation("Telegram: generated webhookId {WebhookId} for connection {ConnectionId}", _options.WebhookId, _connectionId);
                }
            }

            // Compute full webhook URL
            var baseUrl = _options.BaseUrl!.TrimEnd('/');
            var webhookUrl = $"{baseUrl}/api/webhook/telegram/{_options.WebhookId}";

            // Generate webhook secret if not configured
            _webhookSecret = _options.WebhookSecret ?? Guid.NewGuid().ToString("N");

            // Register webhook with Telegram
            await _botClient.SetWebhook(
                webhookUrl,
                secretToken: _webhookSecret,
                cancellationToken: ct);

            _logger.LogInformation("Telegram: webhook registered at {Url}", webhookUrl);
        }
        else
        {
            // Polling mode
            _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message]
            };

            _botClient.StartReceiving(
                updateHandler: HandlePollingUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: _pollingCts.Token);

            _logger.LogInformation("Telegram: polling started");
        }
    }

    /// <summary>
    /// Stops the Telegram channel: cancels polling. Webhook mode leaves the webhook
    /// registered — Telegram retries failed deliveries, and StartAsync re-registers
    /// on the next startup anyway.
    /// </summary>
    public Task StopAsync(CancellationToken ct)
    {
        if (_botClient is null)
            return Task.CompletedTask;

        var isWebhook = string.Equals(_options.Mode, "Webhook", StringComparison.OrdinalIgnoreCase);
        if (!isWebhook)
        {
            _pollingCts?.Cancel();
            _pollingCts?.Dispose();
            _pollingCts = null;
            _logger.LogInformation("Telegram: polling stopped");
        }
        else
        {
            _logger.LogInformation("Telegram: webhook left registered for seamless restart");
        }

        return Task.CompletedTask;
    }

    /// <summary>Returns the cached <see cref="ITelegramSender"/> backed by the current bot client.</summary>
    public ITelegramSender CreateSender()
    {
        if (_sender is null)
            throw new InvalidOperationException("Telegram channel is not started.");

        return _sender;
    }

    /// <summary>Polling update handler — forwards to the message handler using the cached sender.</summary>
    private async Task HandlePollingUpdateAsync(
        ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        await _handler!.HandleUpdateAsync(_sender!, update, ct);
    }

    /// <summary>Polling error handler — logs and continues.</summary>
    private Task HandlePollingErrorAsync(
        ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        _logger.LogError(exception, "Telegram polling error from {Source}", source);
        return Task.CompletedTask;
    }
}
