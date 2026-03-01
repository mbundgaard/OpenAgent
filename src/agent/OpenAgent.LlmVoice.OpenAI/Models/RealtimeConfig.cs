namespace OpenAgent.LlmVoice.OpenAI.Models;

public sealed class RealtimeConfig
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-realtime-preview";
    public string BaseUrl { get; set; } = "wss://api.openai.com/v1/realtime";
}
