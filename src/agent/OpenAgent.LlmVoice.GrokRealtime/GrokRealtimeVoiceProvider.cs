using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmVoice.GrokRealtime.Models;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Voice;

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
        new() { Key = "voice", Label = "Voice", Type = "Enum", DefaultValue = "rex",
            Options = ["eve", "ara", "rex", "sal", "leo"] }
    ];

    // Grok's voice endpoint does not accept/document a client-selected model — the server picks.
    public IReadOnlyList<string> Models => [];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<GrokConfig>(configuration, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize Grok provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");

        logger.LogInformation("Grok Realtime voice provider configured");
    }

    public async Task<IVoiceSession> StartSessionAsync(
        Conversation conversation,
        VoiceSessionOptions? options = null,
        CancellationToken ct = default)
    {
        if (_config is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        logger.LogDebug("Starting Grok voice session for conversation {ConversationId}", conversation.Id);

        var session = new GrokVoiceSession(_config, conversation, agentLogic, options, logger);
        await session.ConnectAsync(ct);

        logger.LogInformation("Grok voice session {SessionId} started for conversation {ConversationId}",
            session.SessionId, conversation.Id);
        return session;
    }
}
