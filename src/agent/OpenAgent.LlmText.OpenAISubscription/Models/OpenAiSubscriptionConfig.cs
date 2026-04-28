using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.OpenAISubscription.Models;

internal sealed class OpenAiSubscriptionConfig
{
    [JsonPropertyName("setupToken")]
    public string SetupToken { get; set; } = "";

    // Skipped during deserialize — the form sends `models` as a comma-separated string,
    // and `Configure` parses it into this array via JsonElement.GetProperty + Split.
    [JsonIgnore]
    public string[] Models { get; set; } = [];

}
