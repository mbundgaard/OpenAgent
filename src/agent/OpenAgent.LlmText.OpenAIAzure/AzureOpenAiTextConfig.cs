namespace OpenAgent.LlmText.OpenAIAzure;

internal sealed class AzureOpenAiTextConfig
{
    public string ApiKey { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ApiVersion { get; set; } = "2024-06-01";
}
