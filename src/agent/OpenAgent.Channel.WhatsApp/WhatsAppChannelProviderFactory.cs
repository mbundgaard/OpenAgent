using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Creates <see cref="WhatsAppChannelProvider"/> instances from connection configuration.
/// </summary>
public sealed class WhatsAppChannelProviderFactory : IChannelProviderFactory
{
    private readonly IConversationStore _store;
    private readonly ILlmTextProvider _textProvider;
    private readonly string _providerKey;
    private readonly string _model;
    private readonly AgentEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>The connection type this factory handles.</summary>
    public string Type => "whatsapp";

    /// <summary>
    /// Creates a new WhatsAppChannelProviderFactory.
    /// </summary>
    /// <param name="store">Conversation store for persistence.</param>
    /// <param name="textProvider">LLM text provider for completions.</param>
    /// <param name="providerKey">Provider key (e.g. "azure-openai-text").</param>
    /// <param name="model">Model name (e.g. "gpt-5.2-chat").</param>
    /// <param name="environment">Agent environment with data path.</param>
    /// <param name="loggerFactory">Logger factory for creating typed loggers.</param>
    public WhatsAppChannelProviderFactory(
        IConversationStore store,
        ILlmTextProvider textProvider,
        string providerKey,
        string model,
        AgentEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _textProvider = textProvider;
        _providerKey = providerKey;
        _model = model;
        _environment = environment;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Deserializes the connection's config into WhatsAppOptions and creates the provider.</summary>
    public IChannelProvider Create(Connection connection)
    {
        var options = JsonSerializer.Deserialize<WhatsAppOptions>(connection.Config,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialize WhatsApp config for connection '{connection.Id}'.");

        var authDir = Path.Combine(_environment.DataPath, "connections", "whatsapp", connection.Id);

        return new WhatsAppChannelProvider(
            options,
            connection.Id,
            authDir,
            _store,
            _textProvider,
            _providerKey,
            _model,
            _loggerFactory);
    }
}
