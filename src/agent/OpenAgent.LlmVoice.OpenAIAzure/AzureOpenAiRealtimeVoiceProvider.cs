using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.LlmVoice.OpenAIAzure.Models;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Conversations;

namespace OpenAgent.LlmVoice.OpenAIAzure;

/// <summary>
/// Voice provider that connects to the Azure OpenAI Realtime API over WebSockets.
/// Requires apiKey, resourceName, and deploymentName to be configured before use.
/// </summary>
public sealed class AzureOpenAiRealtimeVoiceProvider(IAgentLogic agentLogic) : ILlmVoiceProvider
{
    private AzureRealtimeConfig? _config;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new ProviderConfigField
        {
            Key = "apiKey",
            Label = "API Key",
            Type = "Secret",
            Required = true
        },
        new ProviderConfigField
        {
            Key = "resourceName",
            Label = "Resource Name",
            Type = "String",
            Required = true
        },
        new ProviderConfigField
        {
            Key = "deploymentName",
            Label = "Deployment Name",
            Type = "String",
            Required = true
        },
        new ProviderConfigField
        {
            Key = "apiVersion",
            Label = "API Version",
            Type = "String",
            DefaultValue = "2025-04-01-preview"
        }
    ];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<AzureRealtimeConfig>(configuration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(_config.ResourceName))
            throw new InvalidOperationException("resourceName is required.");
        if (string.IsNullOrWhiteSpace(_config.DeploymentName))
            throw new InvalidOperationException("deploymentName is required.");
    }

    public async Task<IVoiceSession> StartSessionAsync(Conversation conversation, CancellationToken ct = default)
    {
        if (_config is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var session = new AzureOpenAiVoiceSession(_config, conversation, agentLogic);
        await session.ConnectAsync(ct);
        return session;
    }
}
