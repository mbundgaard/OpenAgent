# OpenAgent3

Multi-channel AI agent platform. Connects LLM providers (text, voice) to inbound channels (REST API, WebSocket, Telegram, webhooks) with a shared agent personality layer.

## Tech Stack

- .NET 10, ASP.NET Core Minimal APIs, System.Text.Json
- xUnit + WebApplicationFactory for integration tests
- Central Package Management (Directory.Packages.props)

## Project Structure

```
src/agent/
  OpenAgent/                              Host — Program.cs, DI wiring, AgentLogic, VoiceSessionManager
  OpenAgent.Api/                          HTTP/WebSocket endpoints (no business logic)
    Endpoints/                            All endpoint files live here
      ConversationEndpoints.cs            List, get, delete conversations
      ChatEndpoints.cs                    Synchronous text completion (REST)
      WebSocketVoiceEndpoints.cs          Bidirectional voice streaming (WebSocket)
      WebSocketTextEndpoints.cs           Bidirectional text chat (WebSocket)
  OpenAgent.Contracts/                    Interfaces — IAgentLogic, IConversationStore, ILlmTextProvider, ILlmVoiceProvider, IVoiceSessionManager
  OpenAgent.Models/                       Shared models — Conversation, Message, ConversationType, voice events
  OpenAgent.ConversationStore.InMemory/   In-memory store for dev/test
  OpenAgent.LlmText.OpenAIAzure/         Azure OpenAI Chat Completions provider
  OpenAgent.LlmVoice.OpenAIAzure/        Azure OpenAI Realtime voice provider
  OpenAgent.Tests/                        Integration tests
docs/plans/                               Design docs and implementation plans
```

## Architecture Rules

### IAgentLogic is injected context, NOT an orchestrator
IAgentLogic provides system prompt, tools, message history, and tool execution. It is injected INTO LLM providers. Providers call the shots — they call `agentLogic.AddMessage()`, `GetMessages()`, `SystemPrompt`, `Tools`, and `ExecuteToolAsync()`. AgentLogic does not process messages or orchestrate completions.

### Provider pattern
Three provider types, all with IConfigurable:
- **ILlmTextProvider** — stateless text completion (request/response)
- **ILlmVoiceProvider** — creates bidirectional voice sessions
- **IChannelProvider** (future) — inbound channel adapters (Telegram, etc.)

### Conversations are created implicitly
No dedicated "create conversation" endpoint. Conversations are created on first interaction at any endpoint. Each conversation has:
- **Source** (string) — who initiated it: `"app"`, `"telegram"`, etc.
- **Type** (ConversationType enum) — determines agent behavior and system prompt: `Text`, `Voice`, `Cron`, `WebHook`

### Endpoints are thin
Endpoints validate the request and forward to the provider. No business logic in endpoints.

### Endpoint organization
All endpoints live in `OpenAgent.Api/Endpoints/`. They are ASP.NET Core extension methods on `WebApplication`. Grouped by transport and domain:
- REST endpoints: `ConversationEndpoints`, `ChatEndpoints`
- WebSocket endpoints: `WebSocketVoiceEndpoints`, `WebSocketTextEndpoints`

### Interface segregation for cross-project dependencies
When `OpenAgent.Api` needs a type from the host project, extract an interface into `OpenAgent.Contracts`. Example: `IVoiceSessionManager` lives in Contracts, concrete `VoiceSessionManager` lives in the host, DI wires them.

## Coding Conventions

### Naming
- Model/entity properties: `Id` is fine
- Variables and parameters: always explicit — `conversationId`, `userId`, `sessionId`, never bare `id`
- Route parameters match: `{conversationId}` not `{id}`

### Style
- No emojis in code or comments
- XML doc comments on public classes and their public methods
- DRY, YAGNI — no premature abstractions
- `[JsonPropertyName]` attributes on all serialized models, never anonymous types for API payloads

### Git
- Commit frequently — after each logical change, not in accumulated batches
- Concise commit messages focused on "why"

## Build and Test

```bash
cd src/agent && dotnet build
cd src/agent && dotnet test
```

## Key Design Decisions

- ConversationType drives system prompt selection — the agent behaves differently for voice vs text vs cron
- WebSocket is just transport — which LLM a WebSocket endpoint uses depends on the route, not the protocol
- VoiceSessionManager is pure session lifecycle (create, track, close) — no conversation state updates
- Text provider has a tool call loop with a 10-round safety cap
- In-memory conversation store is for dev/test only — production will need a persistent implementation
