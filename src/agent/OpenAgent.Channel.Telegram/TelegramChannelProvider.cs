using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
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
    private readonly string _conversationId;
    private readonly IConversationStore _store;
    private readonly ILlmTextProvider _textProvider;
    private readonly ILogger<TelegramChannelProvider> _logger;
    private readonly ILogger<TelegramMessageHandler> _handlerLogger;

    private TelegramBotClient? _botClient;
    private TelegramMessageHandler? _handler;
    private CancellationTokenSource? _pollingCts;
    private string? _webhookSecret;

    /// <summary>The underlying bot client, or null if the channel is disabled.</summary>
    public TelegramBotClient? BotClient => _botClient;

    /// <summary>The message handler that processes Telegram updates.</summary>
    public TelegramMessageHandler? Handler => _handler;

    /// <summary>The webhook secret used to validate inbound webhook requests.</summary>
    public string? WebhookSecret => _webhookSecret;

    public TelegramChannelProvider(
        TelegramOptions options,
        string conversationId,
        IConversationStore store,
        ILlmTextProvider textProvider,
        ILogger<TelegramChannelProvider> logger,
        ILogger<TelegramMessageHandler> handlerLogger)
    {
        _options = options;
        _conversationId = conversationId;
        _store = store;
        _textProvider = textProvider;
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

        // Initialize bot client and handler
        _botClient = new TelegramBotClient(_options.BotToken);
        _handler = new TelegramMessageHandler(_store, _textProvider, _conversationId, _options, _handlerLogger);

        if (isWebhook)
        {
            if (string.IsNullOrEmpty(_options.WebhookUrl))
                throw new InvalidOperationException(
                    "Telegram WebhookUrl is required when Mode is 'Webhook'.");

            // Generate webhook secret if not configured
            _webhookSecret = _options.WebhookSecret ?? Guid.NewGuid().ToString("N");

            // Register webhook with Telegram
            await _botClient.SetWebhook(
                _options.WebhookUrl,
                secretToken: _webhookSecret,
                cancellationToken: ct);

            _logger.LogInformation("Telegram: webhook registered at {Url}", _options.WebhookUrl);
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
    /// Stops the Telegram channel: deletes the webhook or cancels polling.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        var isWebhook = string.Equals(_options.Mode, "Webhook", StringComparison.OrdinalIgnoreCase);

        if (_botClient is null)
            return;

        if (isWebhook)
        {
            await _botClient.DeleteWebhook(cancellationToken: ct);
            _logger.LogInformation("Telegram: webhook deleted");
        }
        else
        {
            _pollingCts?.Cancel();
            _pollingCts?.Dispose();
            _pollingCts = null;
            _logger.LogInformation("Telegram: polling stopped");
        }
    }

    /// <summary>Creates an <see cref="ITelegramSender"/> backed by the current bot client.</summary>
    public ITelegramSender CreateSender()
    {
        if (_botClient is null)
            throw new InvalidOperationException("Telegram channel is not started.");

        return new TelegramBotClientSender(_botClient, _options.BotToken!);
    }

    /// <summary>Polling update handler — wraps the bot client in a sender and forwards to the message handler.</summary>
    private async Task HandlePollingUpdateAsync(
        ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        var sender = new TelegramBotClientSender(botClient, _options.BotToken!);
        await _handler!.HandleUpdateAsync(sender, update, ct);
    }

    /// <summary>Polling error handler — logs and continues.</summary>
    private Task HandlePollingErrorAsync(
        ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        _logger.LogError(exception, "Telegram polling error from {Source}", source);
        return Task.CompletedTask;
    }
}
