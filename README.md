# OpenAgent

Multi-channel AI agent platform. Connects LLM providers (text, voice) to inbound channels (REST API, WebSocket, Telegram) with a shared agent personality layer.

## Architecture

```
Channels                    Core                         Providers
-----------                 ----                         ---------
REST API      ──┐                                ┌──  Azure OpenAI (Text)
WebSocket     ──┼──  AgentLogic / Contracts  ────┼──  Azure OpenAI (Voice)
Telegram      ──┘                                └──  (pluggable)
```

- **Channels** receive inbound messages and deliver responses
- **AgentLogic** provides system prompt, tools, message history, and tool execution
- **Providers** call the LLM and drive the completion loop — AgentLogic is injected context, not an orchestrator

## Tech Stack

- .NET 10, ASP.NET Core Minimal APIs
- SQLite conversation persistence
- xUnit + WebApplicationFactory for integration tests
- Central Package Management (`Directory.Packages.props`)

## Project Structure

```
src/agent/
  OpenAgent/                              Host — Program.cs, DI wiring, AgentLogic
  OpenAgent.Api/                          HTTP/WebSocket endpoints
  OpenAgent.Channel.Telegram/             Telegram bot channel (webhook, streaming drafts)
  OpenAgent.Contracts/                    Interfaces — IAgentLogic, IConversationStore, ILlmTextProvider, etc.
  OpenAgent.Models/                       Shared models — Conversation, Message, CompletionEvent
  OpenAgent.ConversationStore.Sqlite/     SQLite persistent store with schema migration
  OpenAgent.ConfigStore.File/             File-based provider configuration
  OpenAgent.LlmText.OpenAIAzure/          Azure OpenAI Chat Completions provider
  OpenAgent.LlmVoice.OpenAIAzure/         Azure OpenAI Realtime voice provider
  OpenAgent.Security.ApiKey/              API key authentication
  OpenAgent.Tools.FileSystem/             File tools (read, write, append, edit)
  OpenAgent.Tools.Shell/                  Shell exec tool
  OpenAgent.Tools.WebFetch/               Web fetch tool
  OpenAgent.Tests/                        Integration tests
src/chat-cli/
  OpenAgent.ChatCli/                      Interactive CLI client
src/web/                                  AgentOS web desktop (React)
```

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An Azure OpenAI deployment (or compatible OpenAI endpoint)

### Build and Run

```bash
cd src/agent
dotnet build
dotnet run --project OpenAgent
```

### Run Tests

```bash
cd src/agent
dotnet test
```

### Configuration

The agent loads provider configuration from the file-based config store. Authentication is via API key — set `Authentication__ApiKey` in environment variables or `appsettings.Development.json` for local development.

System prompt is composed from markdown files in the data directory: `AGENTS.md`, `SOUL.md`, `IDENTITY.md`, `USER.md`, `TOOLS.md`, `VOICE.md`.

## API Endpoints

| Endpoint | Transport | Description |
|---|---|---|
| `POST /api/conversations/{id}/messages` | REST | Send a message, receive completion events as JSON array |
| `/ws/conversations/{id}/text` | WebSocket | Bidirectional text chat with streaming |
| `/ws/conversations/{id}/voice` | WebSocket | Bidirectional voice streaming |
| `POST /api/telegram/webhook/{connectionId}` | REST | Telegram bot webhook |
| `GET /api/conversations` | REST | List conversations |
| `GET /api/conversations/{id}` | REST | Get conversation with messages |
| `DELETE /api/conversations/{id}` | REST | Delete conversation |
| `GET /health` | REST | Health check (anonymous) |

## Key Concepts

- **Conversations** are created implicitly — the client generates the ID and passes it in the URL
- **CompletionEvent** is the universal output type: `TextDelta`, `ToolCallEvent`, `ToolResultEvent`, `AssistantMessageSaved`
- **Tools** are self-contained (`ITool`) or grouped under handlers (`IToolHandler`) — scoped to the data directory
- **ConversationType** (Text, Voice, Cron, WebHook) drives system prompt selection and agent behavior

## License

Proprietary.
