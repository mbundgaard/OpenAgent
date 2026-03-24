using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using OpenAgent.Models.Providers;

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

    /// <summary>Human-readable name for this channel type.</summary>
    public string DisplayName => "WhatsApp";

    /// <summary>Configuration fields required to set up this channel type.</summary>
    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } = [];

    /// <summary>Optional post-creation setup step.</summary>
    public ChannelSetupStep? SetupStep => new()
    {
        Type = "qr-code",
        Endpoint = "/api/connections/{id}/whatsapp/qr"
    };

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
        // Parse config manually — the dynamic form sends comma-separated strings,
        // not JSON arrays, so we handle both formats gracefully.
        var options = new WhatsAppOptions();

        if (connection.Config.ValueKind == JsonValueKind.Object)
        {
            if (connection.Config.TryGetProperty("allowedChatIds", out var chatIdsEl))
            {
                if (chatIdsEl.ValueKind == JsonValueKind.Array)
                {
                    options.AllowedChatIds = chatIdsEl.EnumerateArray()
                        .Select(e => e.GetString() ?? "")
                        .Where(s => s.Length > 0)
                        .ToList();
                }
                else if (chatIdsEl.ValueKind == JsonValueKind.String)
                {
                    var raw = chatIdsEl.GetString() ?? "";
                    options.AllowedChatIds = raw.Length > 0
                        ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                        : [];
                }
            }
        }

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
