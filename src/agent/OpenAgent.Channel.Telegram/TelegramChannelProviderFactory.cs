using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;
using OpenAgent.Models.Providers;

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

    public string DisplayName => "Telegram";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "botToken", Label = "Bot Token", Type = "Secret", Required = true },
        new() { Key = "mode", Label = "Mode", Type = "Enum", DefaultValue = "Polling", Options = ["Polling", "Webhook"] },
        new() { Key = "webhookUrl", Label = "Webhook URL", Type = "String" },
        new() { Key = "streamResponses", Label = "Stream Responses", Type = "Enum", DefaultValue = "true", Options = ["true", "false"] },
    ];

    public ChannelSetupStep? SetupStep => null;

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
        // Parse config manually — the dynamic form sends comma-separated strings
        // for list fields and string "true"/"false" for booleans.
        var options = new TelegramOptions();

        if (connection.Config.ValueKind == JsonValueKind.Object)
        {
            if (connection.Config.TryGetProperty("botToken", out var tokenEl) && tokenEl.ValueKind == JsonValueKind.String)
                options.BotToken = tokenEl.GetString();

            if (connection.Config.TryGetProperty("allowedUserIds", out var idsEl))
            {
                if (idsEl.ValueKind == JsonValueKind.Array)
                    options.AllowedUserIds = idsEl.EnumerateArray()
                        .Where(e => e.TryGetInt64(out _))
                        .Select(e => e.GetInt64())
                        .ToList();
                else if (idsEl.ValueKind == JsonValueKind.String)
                {
                    var raw = idsEl.GetString() ?? "";
                    options.AllowedUserIds = raw.Length > 0
                        ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(s => long.TryParse(s, out _))
                            .Select(long.Parse)
                            .ToList()
                        : [];
                }
            }

            if (connection.Config.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String)
                options.Mode = modeEl.GetString() ?? "Polling";

            if (connection.Config.TryGetProperty("webhookUrl", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                options.WebhookUrl = urlEl.GetString();

            if (connection.Config.TryGetProperty("webhookSecret", out var secretEl) && secretEl.ValueKind == JsonValueKind.String)
                options.WebhookSecret = secretEl.GetString();

            if (connection.Config.TryGetProperty("streamResponses", out var streamEl))
            {
                if (streamEl.ValueKind == JsonValueKind.True) options.StreamResponses = true;
                else if (streamEl.ValueKind == JsonValueKind.False) options.StreamResponses = false;
                else if (streamEl.ValueKind == JsonValueKind.String)
                    options.StreamResponses = string.Equals(streamEl.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }

            if (connection.Config.TryGetProperty("showThinking", out var thinkEl))
            {
                if (thinkEl.ValueKind == JsonValueKind.True) options.ShowThinking = true;
                else if (thinkEl.ValueKind == JsonValueKind.String)
                    options.ShowThinking = string.Equals(thinkEl.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }
        }

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
