using System.Net.Http.Json;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.LlmText.OpenAIAzure.Models;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Text;

namespace OpenAgent.LlmText.OpenAIAzure;

public sealed class AzureOpenAiTextProvider(IAgentLogic agentLogic) : ILlmTextProvider, IDisposable
{
    private AzureOpenAiTextConfig? _config;
    private HttpClient? _httpClient;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
        new() { Key = "resourceName", Label = "Resource Name", Type = "String", Required = true },
        new() { Key = "deploymentName", Label = "Deployment Name", Type = "String", Required = true },
        new() { Key = "apiVersion", Label = "API Version", Type = "String", DefaultValue = "2024-06-01" }
    ];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<AzureOpenAiTextConfig>(configuration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(_config.ResourceName))
            throw new InvalidOperationException("resourceName is required.");
        if (string.IsNullOrWhiteSpace(_config.DeploymentName))
            throw new InvalidOperationException("deploymentName is required.");

        _httpClient?.Dispose();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://{_config.ResourceName}.openai.azure.com/")
        };
        _httpClient.DefaultRequestHeaders.Add("api-key", _config.ApiKey);
    }

    public void Dispose() => _httpClient?.Dispose();

    public async Task<TextResponse> CompleteAsync(string conversationId, string userInput, CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        // Store user message
        agentLogic.AddMessage(conversationId, new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversationId,
            Role = "user",
            Content = userInput
        });

        // Build request messages: system prompt + conversation history
        var chatMessages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(agentLogic.SystemPrompt))
        {
            chatMessages.Add(new ChatMessage { Role = "system", Content = agentLogic.SystemPrompt });
        }

        foreach (var msg in agentLogic.GetMessages(conversationId))
        {
            chatMessages.Add(new ChatMessage { Role = msg.Role, Content = msg.Content });
        }

        // Build tools
        List<ChatTool>? tools = agentLogic.Tools.Count > 0
            ? agentLogic.Tools.Select(t => new ChatTool
            {
                Function = new ChatFunction
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.Parameters
                }
            }).ToList()
            : null;

        // Completion loop (handles tool calls)
        const int maxToolRounds = 10;
        for (var round = 0; round < maxToolRounds; round++)
        {
            var request = new ChatCompletionRequest
            {
                Messages = chatMessages,
                Tools = tools,
                ToolChoice = tools is not null ? "auto" : null
            };

            var url = $"openai/deployments/{_config.DeploymentName}/chat/completions?api-version={_config.ApiVersion}";
            var httpResponse = await _httpClient.PostAsJsonAsync(url, request, ct);
            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Azure OpenAI returned {(int)httpResponse.StatusCode}: {errorBody}");
            }

            var response = await httpResponse.Content.ReadFromJsonAsync<ChatCompletionResponse>(ct)
                ?? throw new InvalidOperationException("Empty response from Azure OpenAI.");

            var choice = response.Choices?.FirstOrDefault()
                ?? throw new InvalidOperationException("No choices in response.");

            var message = choice.Message
                ?? throw new InvalidOperationException("No message in choice.");

            // If the model wants to call tools
            if (message.ToolCalls is { Count: > 0 })
            {
                // Add assistant message with tool calls to the conversation
                chatMessages.Add(message);

                // Execute each tool call and add results
                foreach (var toolCall in message.ToolCalls)
                {
                    var result = await agentLogic.ExecuteToolAsync(
                        conversationId, toolCall.Function.Name, toolCall.Function.Arguments, ct);

                    chatMessages.Add(new ChatMessage
                    {
                        Role = "tool",
                        Content = result,
                        ToolCallId = toolCall.Id
                    });
                }

                continue; // Re-call the LLM with tool results
            }

            // Final text response
            var content = message.Content ?? "";

            agentLogic.AddMessage(conversationId, new Message
            {
                Id = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Role = "assistant",
                Content = content
            });

            return new TextResponse { Content = content, Role = "assistant" };
        }

        throw new InvalidOperationException($"Tool call loop exceeded {maxToolRounds} rounds.");
    }
}
