using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.LlmText.OpenAIAzure.Models;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Text;

namespace OpenAgent.LlmText.OpenAIAzure;

public sealed class AzureOpenAiTextProvider(IAgentLogic agentLogic, ILogger<AzureOpenAiTextProvider> logger) : ILlmTextProvider, IDisposable
{
    private AzureOpenAiTextConfig? _config;
    private HttpClient? _httpClient;

    public string Key => "text-provider";

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new() { Key = "apiKey", Label = "API Key", Type = "Secret", Required = true },
        new() { Key = "endpoint", Label = "Endpoint", Type = "String", Required = true },
        new() { Key = "deploymentName", Label = "Deployment Name", Type = "String", Required = true },
        new() { Key = "apiVersion", Label = "API Version", Type = "String", DefaultValue = "2025-04-01-preview" }
    ];

    public void Configure(JsonElement configuration)
    {
        _config = JsonSerializer.Deserialize<AzureOpenAiTextConfig>(configuration,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provider configuration.");

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("apiKey is required.");
        if (string.IsNullOrWhiteSpace(_config.Endpoint))
            throw new InvalidOperationException("endpoint is required.");
        if (string.IsNullOrWhiteSpace(_config.DeploymentName))
            throw new InvalidOperationException("deploymentName is required.");

        var baseUri = _config.Endpoint.TrimEnd('/');
        _httpClient?.Dispose();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUri + "/")
        };
        _httpClient.DefaultRequestHeaders.Add("api-key", _config.ApiKey);

        logger.LogInformation("Text provider configured for deployment {DeploymentName} at {Endpoint}",
            _config.DeploymentName, _config.Endpoint);
    }

    public void Dispose() => _httpClient?.Dispose();

    public async Task<TextResponse> CompleteAsync(Conversation conversation, string userInput, CancellationToken ct = default)
    {
        if (_config is null || _httpClient is null)
            throw new InvalidOperationException("Provider has not been configured. Call Configure() first.");

        var conversationId = conversation.Id;
        logger.LogDebug("CompleteAsync called for conversation {ConversationId}", conversationId);

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

        var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            chatMessages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
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
                logger.LogError("Azure OpenAI returned {StatusCode} for conversation {ConversationId}: {ErrorBody}",
                    (int)httpResponse.StatusCode, conversationId, errorBody);
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
                logger.LogDebug("Tool calls requested in conversation {ConversationId}: {ToolNames}",
                    conversationId, string.Join(", ", message.ToolCalls.Select(t => t.Function.Name)));

                // Add assistant message with tool calls to the conversation
                chatMessages.Add(message);

                // Execute each tool call and add results
                foreach (var toolCall in message.ToolCalls)
                {
                    logger.LogDebug("Executing tool {ToolName} for conversation {ConversationId}",
                        toolCall.Function.Name, conversationId);
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

            logger.LogDebug("Completion finished for conversation {ConversationId}, {ContentLength} chars",
                conversationId, content.Length);
            return new TextResponse { Content = content, Role = "assistant" };
        }

        logger.LogError("Tool call loop exceeded {MaxRounds} rounds for conversation {ConversationId}",
            maxToolRounds, conversationId);
        throw new InvalidOperationException($"Tool call loop exceeded {maxToolRounds} rounds.");
    }
}
