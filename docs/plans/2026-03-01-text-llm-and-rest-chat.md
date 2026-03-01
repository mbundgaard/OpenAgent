# Text LLM Provider + REST Chat Endpoint — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add stateless text chat via Azure OpenAI Chat Completions, with message storage on IAgentLogic and a thin REST endpoint.

**Architecture:** IAgentLogic gains AddMessage/GetMessages (backed by IConversationStore). A new ILlmTextProvider contract + Azure OpenAI implementation takes a conversation ID and user input, calls IAgentLogic for all context, runs the completion (including tool loops), and persists messages. The endpoint just forwards.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, System.Text.Json, Azure OpenAI Chat Completions REST API, xUnit + WebApplicationFactory.

**Design doc:** `docs/plans/2026-03-01-text-llm-and-rest-chat-design.md`

---

## Task 1: Add message storage to IConversationStore

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/IConversationStore.cs`
- Modify: `src/agent/OpenAgent.ConversationStore.InMemory/InMemoryConversationStoreProvider.cs`

**Step 1: Add methods to IConversationStore**

In `src/agent/OpenAgent.Contracts/IConversationStore.cs`, add two methods to the interface:

```csharp
void AddMessage(string conversationId, Message message);
IReadOnlyList<Message> GetMessages(string conversationId);
```

**Step 2: Implement in InMemoryConversationStoreProvider**

In `src/agent/OpenAgent.ConversationStore.InMemory/InMemoryConversationStoreProvider.cs`, add a second dictionary and implement:

```csharp
private readonly ConcurrentDictionary<string, List<Message>> _messages = new();

public void AddMessage(string conversationId, Message message)
{
    var list = _messages.GetOrAdd(conversationId, _ => []);
    lock (list) { list.Add(message); }
}

public IReadOnlyList<Message> GetMessages(string conversationId)
{
    return _messages.TryGetValue(conversationId, out var list)
        ? lock (list) { return list.ToList(); }
        : [];
}
```

Note: `lock` expression requires returning a value. Use a block form:

```csharp
public IReadOnlyList<Message> GetMessages(string conversationId)
{
    if (!_messages.TryGetValue(conversationId, out var list))
        return [];
    lock (list) { return list.ToList(); }
}
```

Also update `Delete` to clean up messages:

```csharp
public bool Delete(string id)
{
    _messages.TryRemove(id, out _);
    return _conversations.TryRemove(id, out _);
}
```

**Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```
feat: add message storage to IConversationStore
```

---

## Task 2: Add AddMessage/GetMessages to IAgentLogic

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/IAgentLogic.cs`
- Modify: `src/agent/OpenAgent/AgentLogic.cs`

**Step 1: Extend IAgentLogic interface**

In `src/agent/OpenAgent.Contracts/IAgentLogic.cs`, add:

```csharp
/// <summary>Persists a message in the conversation history.</summary>
void AddMessage(string conversationId, Message message);

/// <summary>Returns the full message history for a conversation.</summary>
IReadOnlyList<Message> GetMessages(string conversationId);
```

Add `using OpenAgent.Models.Conversations;` at the top.

**Step 2: Implement in AgentLogic**

In `src/agent/OpenAgent/AgentLogic.cs`, inject `IConversationStore` and delegate:

```csharp
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;

namespace OpenAgent;

internal sealed class AgentLogic(IConversationStore store) : IAgentLogic
{
    public string SystemPrompt => "";

    public IReadOnlyList<AgentToolDefinition> Tools => [];

    public Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default)
        => Task.FromResult("{}");

    public void AddMessage(string conversationId, Message message)
        => store.AddMessage(conversationId, message);

    public IReadOnlyList<Message> GetMessages(string conversationId)
        => store.GetMessages(conversationId);
}
```

**Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded, 0 errors.

**Step 4: Run existing tests**

Run: `cd src/agent && dotnet test`
Expected: All 6 tests pass (DI resolves AgentLogic(IConversationStore) automatically).

**Step 5: Commit**

```
feat: add AddMessage/GetMessages to IAgentLogic
```

---

## Task 3: Add ILlmTextProvider contract and TextResponse model

**Files:**
- Create: `src/agent/OpenAgent.Contracts/ILlmTextProvider.cs`
- Create: `src/agent/OpenAgent.Models/Text/TextResponse.cs`

**Step 1: Create TextResponse model**

Create `src/agent/OpenAgent.Models/Text/TextResponse.cs`:

```csharp
namespace OpenAgent.Models.Text;

public sealed class TextResponse
{
    public required string Content { get; init; }
    public required string Role { get; init; }
}
```

**Step 2: Create ILlmTextProvider**

Create `src/agent/OpenAgent.Contracts/ILlmTextProvider.cs`:

```csharp
using OpenAgent.Models.Text;

namespace OpenAgent.Contracts;

/// <summary>
/// Stateless text completion provider. Sends conversation history to an LLM and returns the response.
/// The provider calls IAgentLogic for system prompt, tools, message history, and tool execution.
/// </summary>
public interface ILlmTextProvider : IConfigurable
{
    Task<TextResponse> CompleteAsync(string conversationId, string userInput, CancellationToken ct = default);
}
```

**Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```
feat: add ILlmTextProvider contract and TextResponse model
```

---

## Task 4: Create OpenAgent.LlmText.OpenAIAzure project

**Files:**
- Create: `src/agent/OpenAgent.LlmText.OpenAIAzure/OpenAgent.LlmText.OpenAIAzure.csproj`
- Create: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextConfig.cs`
- Create: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- Create: `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ChatCompletionRequest.cs`
- Create: `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ChatCompletionResponse.cs`
- Modify: `src/agent/OpenAgent.sln` (add project)
- Modify: `src/agent/OpenAgent/OpenAgent.csproj` (add reference)

**Step 1: Create project file**

Create `src/agent/OpenAgent.LlmText.OpenAIAzure/OpenAgent.LlmText.OpenAIAzure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Add project to solution**

Run: `cd src/agent && dotnet sln add OpenAgent.LlmText.OpenAIAzure/OpenAgent.LlmText.OpenAIAzure.csproj --solution-folder Providers`

**Step 3: Add project reference from host**

Run: `cd src/agent/OpenAgent && dotnet add reference ../OpenAgent.LlmText.OpenAIAzure/OpenAgent.LlmText.OpenAIAzure.csproj`

**Step 4: Create config model**

Create `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextConfig.cs`:

```csharp
namespace OpenAgent.LlmText.OpenAIAzure;

internal sealed class AzureOpenAiTextConfig
{
    public string ApiKey { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ApiVersion { get; set; } = "2024-06-01";
}
```

**Step 5: Create request/response models**

Create `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ChatCompletionRequest.cs`:

```csharp
using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.OpenAIAzure.Models;

internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("messages")]
    public required List<ChatMessage> Messages { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; set; }
}

internal sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

internal sealed class ChatTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required ChatFunction Function { get; set; }
}

internal sealed class ChatFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public required object Parameters { get; set; }
}

internal sealed class ToolCall
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required ToolCallFunction Function { get; set; }
}

internal sealed class ToolCallFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; set; }
}
```

Create `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ChatCompletionResponse.cs`:

```csharp
using System.Text.Json.Serialization;

namespace OpenAgent.LlmText.OpenAIAzure.Models;

internal sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice>? Choices { get; set; }
}

internal sealed class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}
```

**Step 6: Create the provider**

Create `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using OpenAgent.Contracts;
using OpenAgent.LlmText.OpenAIAzure.Models;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Text;

namespace OpenAgent.LlmText.OpenAIAzure;

public sealed class AzureOpenAiTextProvider(IAgentLogic agentLogic) : ILlmTextProvider
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

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://{_config.ResourceName}.openai.azure.com/")
        };
        _httpClient.DefaultRequestHeaders.Add("api-key", _config.ApiKey);
    }

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
        while (true)
        {
            var request = new ChatCompletionRequest
            {
                Messages = chatMessages,
                Tools = tools,
                ToolChoice = tools is not null ? "auto" : null
            };

            var url = $"openai/deployments/{_config.DeploymentName}/chat/completions?api-version={_config.ApiVersion}";
            var httpResponse = await _httpClient.PostAsJsonAsync(url, request, ct);
            httpResponse.EnsureSuccessStatusCode();

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
    }
}
```

**Step 7: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded, 0 errors.

**Step 8: Commit**

```
feat: add Azure OpenAI text completion provider
```

---

## Task 5: Add REST chat endpoint

**Files:**
- Create: `src/agent/OpenAgent.Api/Chat/ChatEndpoints.cs`
- Modify: `src/agent/OpenAgent/Program.cs`

**Step 1: Create ChatEndpoints**

Create `src/agent/OpenAgent.Api/Chat/ChatEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Api.Chat;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/conversations/{id}/messages", async (
            string id,
            ChatRequest request,
            IConversationStore store,
            ILlmTextProvider textProvider,
            CancellationToken ct) =>
        {
            if (store.Get(id) is null)
                return Results.NotFound();

            var response = await textProvider.CompleteAsync(id, request.Content, ct);

            return Results.Ok(new { response.Role, response.Content });
        });
    }
}

public sealed record ChatRequest(string Content);
```

**Step 2: Register provider and map endpoint in Program.cs**

In `src/agent/OpenAgent/Program.cs`:

- Add `using OpenAgent.Api.Chat;` and `using OpenAgent.LlmText.OpenAIAzure;`
- Add `builder.Services.AddSingleton<ILlmTextProvider, AzureOpenAiTextProvider>();`
- Add `app.MapChatEndpoints();`

Full file after changes:

```csharp
using OpenAgent;
using OpenAgent.Api.Chat;
using OpenAgent.Api.Conversations;
using OpenAgent.Api.Voice;
using OpenAgent.Api.WebSockets;
using OpenAgent.Contracts;
using OpenAgent.ConversationStore.InMemory;
using OpenAgent.LlmText.OpenAIAzure;
using OpenAgent.LlmVoice.OpenAIAzure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IAgentLogic, AgentLogic>();
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStoreProvider>();
builder.Services.AddSingleton<ILlmVoiceProvider, AzureOpenAiRealtimeVoiceProvider>();
builder.Services.AddSingleton<ILlmTextProvider, AzureOpenAiTextProvider>();
builder.Services.AddSingleton<VoiceSessionManager>();

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok());
app.MapConversationEndpoints();
app.MapChatEndpoints();
app.MapWebSocketEndpoints();

app.Run();

namespace OpenAgent
{
    public partial class Program;
}
```

**Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded, 0 errors.

**Step 4: Run existing tests**

Run: `cd src/agent && dotnet test`
Expected: All 6 tests pass (new endpoint doesn't break existing ones).

**Step 5: Commit**

```
feat: add POST /api/conversations/{id}/messages chat endpoint
```

---

## Task 6: Add integration tests for the chat endpoint

**Files:**
- Create: `src/agent/OpenAgent.Tests/ChatEndpointTests.cs`

The tests use WebApplicationFactory. Since the real Azure provider requires configuration and a live API, we test the endpoint wiring: conversation not found returns 404, and a valid request reaches the provider. We override ILlmTextProvider with a fake that returns a canned response.

**Step 1: Write the tests**

Create `src/agent/OpenAgent.Tests/ChatEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;
using OpenAgent.Models.Providers;
using OpenAgent.Models.Text;

namespace OpenAgent.Tests;

public class ChatEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ChatEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace real provider with a fake
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmTextProvider));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddSingleton<ILlmTextProvider, FakeTextProvider>();
            });
        });
    }

    [Fact]
    public async Task SendMessage_ConversationNotFound_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/conversations/does-not-exist/messages",
            new { Content = "hello" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SendMessage_ValidConversation_ReturnsAssistantResponse()
    {
        var client = _factory.CreateClient();

        // Create a conversation first
        var createResponse = await client.PostAsync("/api/conversations", null);
        var created = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        // Send a message
        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{created!.Id}/messages",
            new { Content = "hello" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.Equal("assistant", body!.Role);
        Assert.Equal("fake response", body.Content);
    }

    private record ConversationResponse(string Id);
    private record ChatResponse(string Role, string Content);

    private sealed class FakeTextProvider : ILlmTextProvider
    {
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public void Configure(JsonElement configuration) { }

        public Task<TextResponse> CompleteAsync(string conversationId, string userInput, CancellationToken ct = default)
        {
            return Task.FromResult(new TextResponse { Role = "assistant", Content = "fake response" });
        }
    }
}
```

**Step 2: Run tests**

Run: `cd src/agent && dotnet test`
Expected: All 8 tests pass (6 existing + 2 new).

**Step 3: Commit**

```
test: add integration tests for chat endpoint
```

---

## Task 7: Final verification

**Step 1: Clean build**

Run: `cd src/agent && dotnet build`
Expected: 0 warnings, 0 errors.

**Step 2: All tests**

Run: `cd src/agent && dotnet test`
Expected: 8 tests pass.

**Step 3: Verify project structure**

Confirm these files exist:
- `src/agent/OpenAgent.Contracts/ILlmTextProvider.cs`
- `src/agent/OpenAgent.Models/Text/TextResponse.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextConfig.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ChatCompletionRequest.cs`
- `src/agent/OpenAgent.LlmText.OpenAIAzure/Models/ChatCompletionResponse.cs`
- `src/agent/OpenAgent.Api/Chat/ChatEndpoints.cs`
- `src/agent/OpenAgent.Tests/ChatEndpointTests.cs`
