using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmVoice.GeminiLive.Models;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;

namespace OpenAgent.LlmVoice.GeminiLive;

/// <summary>
/// Voice provider that connects to the Google Gemini Live API over WebSockets.
/// Uses Gemini's proprietary BidiGenerateContent protocol (not OpenAI-compatible).
/// Handles the 15-minute session cap via proactive reconnect at a configurable threshold.
/// </summary>
public sealed class GeminiLiveVoiceProvider(IAgentLogic agentLogic, ILogger<GeminiLiveVoiceProvider> logger) : ILlmVoiceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private GeminiConfig? _config;

    public const string ProviderKey = "gemini-live-voice";

    public string Key => ProviderKey;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
        new() { Key = "models", Label = "Models (comma-separated)", Type = "String", Required = true,
            DefaultValue = "models/gemini-live-2.5-flash-native-audio" },
        new() { Key = "voice", Label = "Voice", Type = "Enum", DefaultValue = "Puck",
            Options = ["Puck", "Charon", "Kore", "Fenrir", "Aoede", "Leda", "Orus", "Zephyr"] },
        new() { Key = "reconnectAfterMinutes", Label = "Reconnect After (minutes)", Type = "String", DefaultValue = "13" }
    ];

    public IReadOnlyList<string> Models => _config?.Models ?? [];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<GeminiConfig>(configuration, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize Gemini Live provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");

        if (configuration.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.String)
        {
            _config.Models = modelsProp.GetString()!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (configuration.TryGetProperty("reconnectAfterMinutes", out var reconnectProp) &&
            reconnectProp.ValueKind == JsonValueKind.String &&
            int.TryParse(reconnectProp.GetString(), out var minutes))
        {
            _config.ReconnectAfterMinutes = minutes;
        }

        logger.LogInformation(
            "Gemini Live voice provider configured with {ModelCount} model(s), reconnect after {Minutes} min",
            _config.Models.Length, _config.ReconnectAfterMinutes);
    }

    public async Task<IVoiceSession> StartSessionAsync(Conversation conversation, CancellationToken ct = default)
    {
        if (_config is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        logger.LogDebug("Starting Gemini Live session for conversation {ConversationId} with model {Model}",
            conversation.Id, conversation.Model);

        var session = new GeminiLiveVoiceSession(_config, conversation, agentLogic, logger);
        await session.ConnectAsync(ct);

        logger.LogInformation("Gemini Live session {SessionId} started for conversation {ConversationId}",
            session.SessionId, conversation.Id);
        return session;
    }
}
