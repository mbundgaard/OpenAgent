# OpenAgent

Multi-channel AI agent platform. Connects LLM providers to inbound channels with a shared agent personality, persistent conversations, and an extensible skill system.

**One agent, any channel, any LLM.** Build an AI agent once — reach it via REST API, WebSocket, Telegram, or WhatsApp. Switch LLM providers per conversation without losing context. Teach it new workflows by dropping a skill folder.

## Features

- **Multi-channel** — REST API, WebSocket (text + voice), Telegram (polling + webhook with streaming drafts), WhatsApp (Baileys bridge)
- **Multi-provider** — Azure OpenAI and Anthropic Claude for text, Azure OpenAI Realtime for voice. Switch providers mid-conversation.
- **Agent Skills** — Open [agentskills.io](https://agentskills.io) format. Drop a `SKILL.md` folder into the skills directory and the agent learns new workflows. Compatible with Claude Code, Cursor, VS Code Copilot, and 30+ other clients.
- **Persistent conversations** — SQLite-backed with compaction. Conversations survive restarts, token usage is tracked, and long histories are automatically summarized.
- **Tool system** — File operations, shell execution, web fetch, and skill resource loading. Tools are scoped to the data directory for safety.
- **Personality layer** — System prompt composed from modular markdown files (SOUL.md, IDENTITY.md, USER.md, etc.). Customize the agent's personality without touching code.
- **Web desktop** — React-based AgentOS UI for chat, conversation management, provider settings, and connection configuration.
- **Dynamic configuration** — Provider settings, connections, and skills are managed at runtime via admin API. No restarts needed.

## Architecture

```
Channels                    Core                         Providers
-----------                 ----                         ---------
REST API      ──┐                                ┌──  Azure OpenAI (Text)
WebSocket     ──┤                                ├──  Anthropic Claude (Text)
Telegram      ──┼──  AgentLogic / Contracts  ────┼──  Azure OpenAI (Voice)
WhatsApp      ──┘                                └──  (pluggable)

                         Skills Layer
                         ------------
                    {dataPath}/skills/*/SKILL.md
                    Catalog → Activate → System Prompt
```

- **Channels** receive inbound messages and deliver responses
- **AgentLogic** provides system prompt, tools, message history, and tool execution
- **Providers** call the LLM and drive the completion loop — AgentLogic is injected context, not an orchestrator
- **Skills** are markdown instruction documents that teach the agent specialized workflows. Activated per conversation and injected into the system prompt.

## Tech Stack

- .NET 10, ASP.NET Core Minimal APIs, System.Text.Json
- Node.js (Baileys bridge for WhatsApp Web protocol)
- React 19, TypeScript, Vite, CSS Modules
- SQLite conversation persistence with WAL mode
- xUnit + WebApplicationFactory for integration tests
- Central Package Management (`Directory.Packages.props`)
- Docker container deployed to Azure App Service

## Project Structure

```
src/agent/
  OpenAgent/                              Host — Program.cs, DI wiring, AgentLogic
  OpenAgent.Api/                          HTTP/WebSocket endpoints
  OpenAgent.Contracts/                    Interfaces — IAgentLogic, IConversationStore, ILlmTextProvider, etc.
  OpenAgent.Models/                       Shared models — Conversation, Message, CompletionEvent
  OpenAgent.Skills/                       Agent Skills (agentskills.io spec) — discovery, catalog, activation
  OpenAgent.Channel.Telegram/             Telegram bot (polling + webhook, streaming drafts)
  OpenAgent.Channel.WhatsApp/             WhatsApp (Baileys Node.js bridge, QR pairing)
  OpenAgent.ConversationStore.Sqlite/     SQLite persistent store with schema migration
  OpenAgent.ConfigStore.File/             File-based provider configuration
  OpenAgent.LlmText.OpenAIAzure/          Azure OpenAI Chat Completions provider
  OpenAgent.LlmText.AnthropicSubscription/ Anthropic Messages API provider
  OpenAgent.LlmVoice.OpenAIAzure/         Azure OpenAI Realtime voice provider
  OpenAgent.Compaction/                   Conversation compaction (LLM-driven summarization)
  OpenAgent.Security.ApiKey/              API key authentication
  OpenAgent.Tools.FileSystem/             File tools (read, write, append, edit)
  OpenAgent.Tools.Shell/                  Shell exec tool
  OpenAgent.Tools.WebFetch/               Web fetch tool
  OpenAgent.Tools.Expand/                 Message expansion tool (for compacted history)
  OpenAgent.Tests/                        Integration tests
src/web/                                  AgentOS web desktop (React 19, TypeScript, Vite)
```

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org/) (for web frontend and WhatsApp bridge)

### Build and Run

```bash
# Backend
cd src/agent
dotnet build
dotnet run --project OpenAgent

# Frontend
cd src/web
npm install
npm run dev
```

### Run Tests

```bash
cd src/agent
dotnet test
```

### Configuration

Provider configuration is managed at runtime via the admin API or the AgentOS settings UI. Authentication is via API key — set `Authentication__ApiKey` in environment variables or `appsettings.Development.json` for local development.

System prompt is composed from modular markdown files in the data directory: `AGENTS.md`, `SOUL.md`, `IDENTITY.md`, `USER.md`, `TOOLS.md`, `VOICE.md`, `MEMORY.md`.

### Skills

Drop a skill folder into `{dataPath}/skills/`:

```
skills/
  my-skill/
    SKILL.md          # YAML frontmatter + markdown instructions
    scripts/          # Optional executable code
    references/       # Optional documentation
```

The agent discovers skills at startup and lists them in the system prompt. When a task matches a skill's description, the agent activates it — the full instructions are injected into the system prompt for the conversation. Skills persist across turns and survive compaction.

Format follows the open [Agent Skills specification](https://agentskills.io/specification).

## Deployment

Docker image built via GitHub Actions on every push to master. Deployed to Azure App Service.

```bash
docker pull ghcr.io/mbundgaard/open-agent:latest
docker run -p 8080:8080 -v /data:/home/data ghcr.io/mbundgaard/open-agent:latest
```

## License

Proprietary.
