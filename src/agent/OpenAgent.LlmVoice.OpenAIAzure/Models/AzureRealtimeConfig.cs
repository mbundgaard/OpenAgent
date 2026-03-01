namespace OpenAgent.LlmVoice.OpenAIAzure.Models;

/// <summary>
/// Connection settings for the Azure OpenAI Realtime API (API key, resource, deployment, version).
/// </summary>
public sealed class AzureRealtimeConfig
{
    public string ApiKey { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ApiVersion { get; set; } = "2025-04-01-preview";
}
