using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.OpenAISubscription.Models;

internal sealed class OpenAiSubscriptionConfig
{
    [JsonPropertyName("setupToken")]
    public string SetupToken { get; set; } = "";

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";

    // Unix seconds. 0 = unknown (e.g. legacy configs from before refresh support).
    // Already includes Pi's 5-minute safety margin so callers can use a plain
    // `now >= ExpiresAtUnix` comparison.
    [JsonPropertyName("expiresAt")]
    public long ExpiresAtUnix { get; set; }
}
