using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.LlmVoice.OpenAI.Models;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Voice;

namespace OpenAgent.LlmVoice.OpenAI;

public sealed class OpenAiRealtimeVoiceProvider : ILlmVoiceProvider
{
    private RealtimeOptions? _options;

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
            Key = "model",
            Label = "Model",
            Type = "String",
            DefaultValue = "gpt-4o-realtime-preview"
        },
        new ProviderConfigField
        {
            Key = "baseUrl",
            Label = "Base URL",
            Type = "String",
            DefaultValue = "wss://api.openai.com/v1/realtime"
        }
    ];

    public void Configure(JsonElement configuration)
    {
        _options = JsonSerializer.Deserialize<RealtimeOptions>(configuration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("apiKey is required.");
    }

    public async Task<IVoiceSession> StartSessionAsync(
        VoiceSessionConfig config, CancellationToken ct = default)
    {
        if (_options is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var session = new OpenAiVoiceSession(_options, config);
        await session.ConnectAsync(ct);
        return session;
    }
}
