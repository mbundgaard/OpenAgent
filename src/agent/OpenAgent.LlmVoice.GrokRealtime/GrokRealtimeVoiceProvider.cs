using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmVoice.GrokRealtime.Models;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.LlmVoice.GrokRealtime;

/// <summary>
/// Voice provider that connects to the xAI Grok Realtime API over WebSockets.
/// Protocol-compatible with OpenAI Realtime; requires only an API key (no endpoint config).
/// </summary>
public sealed class GrokRealtimeVoiceProvider(IAgentLogic agentLogic, ILogger<GrokRealtimeVoiceProvider> logger) : ILlmVoiceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private GrokConfig? _config;

    public const string ProviderKey = "grok-realtime-voice";

    public string Key => ProviderKey;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
        new() { Key = "models", Label = "Models (comma-separated)", Type = "String", Required = true,
            DefaultValue = "grok-4-1-fast-non-reasoning" },
        new() { Key = "voice", Label = "Voice", Type = "Enum", DefaultValue = "eve",
            Options = ["eve", "aria", "kai", "nova", "rex"] }
    ];

    public IReadOnlyList<string> Models => _config?.Models ?? [];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<GrokConfig>(configuration, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize Grok provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");

        if (configuration.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.String)
        {
            _config.Models = modelsProp.GetString()!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        logger.LogInformation("Grok Realtime voice provider configured with {ModelCount} model(s)", _config.Models.Length);
    }

    public async Task<IVoiceSession> StartSessionAsync(Conversation conversation, CancellationToken ct = default)
    {
        if (_config is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        logger.LogDebug("Starting Grok voice session for conversation {ConversationId} with model {Model}",
            conversation.Id, conversation.Model);

        var session = new GrokVoiceSession(_config, conversation, agentLogic, logger);
        await session.ConnectAsync(ct);

        logger.LogInformation("Grok voice session {SessionId} started for conversation {ConversationId}",
            session.SessionId, conversation.Id);
        return session;
    }
}
