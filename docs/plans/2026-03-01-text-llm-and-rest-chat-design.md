# Text LLM Provider + REST Chat Endpoint

## Context

OpenAgent has a working voice pipeline (Azure OpenAI Realtime over WebSocket) with `IAgentLogic` injected into the voice provider. We need the same pattern for text: a stateless request/response text LLM provider with a REST endpoint to drive it.

## Architecture

Three provider interface types, each with swappable implementations:

- **ILlmTextProvider** — text completions (Azure OpenAI Chat, future: others)
- **ILlmVoiceProvider** — realtime voice (Azure OpenAI Realtime, existing)
- **IChannelProvider** — inbound messaging (Telegram, future scope)

All LLM providers receive `IAgentLogic` via DI. The provider calls into `IAgentLogic` for everything: system prompt, tools, tool execution, and message storage/retrieval.

### Data Flow

```
Endpoint (thin)                    ILlmTextProvider                    IAgentLogic
    |                                    |                                 |
    |  CompleteAsync(convId, input) -->  |                                 |
    |                                    |  AddMessage(convId, user msg) -->|-> IConversationStore
    |                                    |  GetMessages(convId) ---------->|-> IConversationStore
    |                                    |  .SystemPrompt ---------------->|
    |                                    |  .Tools ----------------------->|
    |                                    |                                 |
    |                                    |-> Azure OpenAI Chat API         |
    |                                    |                                 |
    |                                    |  (tool loop if needed)          |
    |                                    |  ExecuteToolAsync() ----------->|
    |                                    |  <-- tool result                |
    |                                    |-> Azure OpenAI Chat API         |
    |                                    |                                 |
    |                                    |  AddMessage(convId, asst msg) ->|-> IConversationStore
    |  <-- TextResponse                  |                                 |
```

## Design Decisions

1. **IAgentLogic owns message lifecycle.** The provider calls `AddMessage()` and `GetMessages()` on `IAgentLogic`, which wraps `IConversationStore`. This keeps the endpoint thin and makes every channel adapter identical: forward user input to the provider, return the response.

2. **Text is stateless request/response.** Unlike voice (which requires a persistent WebSocket session), each text completion sends the full conversation history. The conversation store provides continuity between calls.

3. **Tool loops are internal to the provider.** If the LLM requests a tool call, the provider executes it via `IAgentLogic.ExecuteToolAsync()`, appends the result, and re-calls the LLM. This repeats until the LLM produces a final text response.

4. **Endpoint is dead simple.** Validate the request, call `ILlmTextProvider.CompleteAsync(conversationId, userInput)`, return the response. No message assembly, no orchestration.

## Contract Changes

### IAgentLogic (modified)

```csharp
public interface IAgentLogic
{
    string SystemPrompt { get; }
    IReadOnlyList<AgentToolDefinition> Tools { get; }
    Task<string> ExecuteToolAsync(string conversationId, string name, string arguments, CancellationToken ct = default);

    // New
    void AddMessage(string conversationId, Message message);
    IReadOnlyList<Message> GetMessages(string conversationId);
}
```

### ILlmTextProvider (new)

```csharp
public interface ILlmTextProvider : IConfigurable
{
    Task<TextResponse> CompleteAsync(string conversationId, string userInput, CancellationToken ct = default);
}
```

### TextResponse (new model)

```csharp
public sealed class TextResponse
{
    public required string Content { get; init; }
    public required string Role { get; init; }
}
```

## Project Changes

| Action | Project | Change |
|--------|---------|--------|
| Modify | OpenAgent.Contracts | Add `ILlmTextProvider`, add `AddMessage`/`GetMessages` to `IAgentLogic` |
| Modify | OpenAgent.Models | Add `TextResponse` |
| Create | OpenAgent.LlmText.OpenAIAzure | Azure OpenAI Chat Completions implementation |
| Modify | OpenAgent | `AgentLogic` implements new methods, wraps `IConversationStore` |
| Modify | OpenAgent.Api | New `POST /api/conversations/{id}/messages` endpoint |
| Modify | OpenAgent.ConversationStore.InMemory | Add message storage |
| Modify | OpenAgent (Program.cs) | Register `ILlmTextProvider` |

## REST Endpoint

```
POST /api/conversations/{id}/messages
Body: { "content": "hello" }
Response: { "id": "...", "role": "assistant", "content": "...", "createdAt": "..." }
```

Returns 404 if conversation doesn't exist.

## Future

- **IChannelProvider** (Telegram) will follow the same pattern: receive inbound message, call `ILlmTextProvider.CompleteAsync()`, send response back through the channel.
- **Streaming** can be added later via a separate endpoint or WebSocket text protocol.
