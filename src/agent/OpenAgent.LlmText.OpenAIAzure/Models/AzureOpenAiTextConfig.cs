namespace OpenAgent.LlmText.OpenAIAzure.Models;

internal sealed class AzureOpenAiTextConfig
{
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ApiVersion { get; set; } = "2025-04-01-preview";
}
