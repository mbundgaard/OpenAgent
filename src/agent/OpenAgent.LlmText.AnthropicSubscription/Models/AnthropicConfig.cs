namespace OpenAgent.LlmText.AnthropicSubscription.Models;

internal sealed class AnthropicConfig
{
    public string SetupToken { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Models { get; set; } = [];
    public int MaxTokens { get; set; } = 16000;
}
