using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmVoice.OpenAIAzure.Models;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Conversations;

namespace OpenAgent.LlmVoice.OpenAIAzure;

/// <summary>
/// Voice provider that connects to the Azure OpenAI Realtime API over WebSockets.
/// Requires apiKey, endpoint, and at least one model to be configured before use.
/// </summary>
public sealed class AzureOpenAiRealtimeVoiceProvider(IAgentLogic agentLogic, ILogger<AzureOpenAiRealtimeVoiceProvider> logger) : ILlmVoiceProvider
{
    private AzureRealtimeConfig? _config;

    public const string ProviderKey = "azure-openai-voice";

    public string Key => ProviderKey;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
        new() { Key = "endpoint", Label = "Endpoint", Type = "String", Required = true },
        new() { Key = "models", Label = "Models (comma-separated)", Type = "String", Required = true },
        new() { Key = "apiVersion", Label = "API Version", Type = "String", DefaultValue = "2025-04-01-preview" },
        new() { Key = "voice", Label = "Voice", Type = "Enum", DefaultValue = "alloy",
            Options = ["alloy", "ash", "ballad", "cedar", "coral", "echo", "marin", "sage", "shimmer", "verse"] }
    ];

    public IReadOnlyList<string> Models => _config?.Models ?? [];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<AzureRealtimeConfig>(configuration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(_config.Endpoint))
            throw new InvalidOperationException("endpoint is required.");

        // Parse models from comma-separated string if provided as a single string
        if (configuration.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.String)
        {
            _config.Models = modelsProp.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        logger.LogInformation("Voice provider configured with {ModelCount} models at {Endpoint}",
            _config.Models.Length, _config.Endpoint);
    }

    public async Task<IVoiceSession> StartSessionAsync(Conversation conversation, CancellationToken ct = default)
    {
        if (_config is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        logger.LogDebug("Starting voice session for conversation {ConversationId} with model {Model}",
            conversation.Id, conversation.Model);
        var session = new AzureOpenAiVoiceSession(_config, conversation, agentLogic, logger);
        await session.ConnectAsync(ct);
        logger.LogInformation("Voice session {SessionId} started for conversation {ConversationId}",
            session.SessionId, conversation.Id);
        return session;
    }
}
