using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.OpenAISubscription.Models;

internal sealed class OpenAiSubscriptionConfig
{
    [JsonPropertyName("setupToken")]
    public string SetupToken { get; set; } = "";
}
