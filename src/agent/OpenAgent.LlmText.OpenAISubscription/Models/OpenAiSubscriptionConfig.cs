using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.OpenAISubscription.Models;

internal sealed class OpenAiSubscriptionConfig
{
    [JsonPropertyName("setupToken")]
    public string SetupToken { get; set; } = "";

    [JsonPropertyName("models")]
    public string[] Models { get; set; } = [];

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://chatgpt.com/backend-api";
}
