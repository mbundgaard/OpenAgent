using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Creates <see cref="TelegramChannelProvider"/> instances from connection configuration.
/// </summary>
public sealed class TelegramChannelProviderFactory : IChannelProviderFactory
{
    private readonly IConversationStore _store;
    private readonly ILlmTextProvider _textProvider;
    private readonly string _providerKey;
    private readonly string _model;
    private readonly ILoggerFactory _loggerFactory;

    public string Type => "telegram";

    public TelegramChannelProviderFactory(
        IConversationStore store,
        ILlmTextProvider textProvider,
        string providerKey,
        string model,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _textProvider = textProvider;
        _providerKey = providerKey;
        _model = model;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Deserializes the connection's config into TelegramOptions and creates the provider.</summary>
    public IChannelProvider Create(Connection connection)
    {
        var options = JsonSerializer.Deserialize<TelegramOptions>(connection.Config,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialize Telegram config for connection '{connection.Id}'.");

        return new TelegramChannelProvider(
            options,
            connection.Id,
            _store,
            _textProvider,
            _providerKey,
            _model,
            _loggerFactory.CreateLogger<TelegramChannelProvider>(),
            _loggerFactory.CreateLogger<TelegramMessageHandler>());
    }
}
