# OpenAgent3

Multi-channel AI agent platform. Connects LLM providers (text, voice) to inbound channels (REST API, WebSocket, Telegram, WhatsApp, webhooks) with a shared agent personality layer.

## Project Board

https://github.com/users/mbundgaard/projects/4 — managed via `gh project` and `gh issue` CLI commands. See [docs/project-api.md](docs/project-api.md) for API reference.

## Tech Stack

- .NET 10, ASP.NET Core Minimal APIs, System.Text.Json
- Node.js (Baileys bridge for WhatsApp Web protocol)
- React 19, TypeScript, Vite, CSS Modules
- xUnit + WebApplicationFactory for integration tests
- Central Package Management (Directory.Packages.props)

## Project Structure

```
src/agent/
  OpenAgent/                              Host — Program.cs, DI wiring, AgentLogic, VoiceSessionManager
  OpenAgent.Api/                          HTTP/WebSocket endpoints (no business logic)
    Endpoints/                            All endpoint files live here
      ConversationEndpoints.cs            List, get, delete conversations
      ChatEndpoints.cs                    Text completion (REST) — returns CompletionEvent JSON array
      WebSocketVoiceEndpoints.cs          Bidirectional voice streaming (WebSocket)
      WebSocketTextEndpoints.cs           Bidirectional text chat (WebSocket) — streams CompletionEvents
  OpenAgent.Contracts/                    Interfaces — IAgentLogic, IConversationStore, ILlmTextProvider, ILlmVoiceProvider, IVoiceSessionManager, ITool, IToolHandler
  OpenAgent.Models/                       Shared models — Conversation, Message, ConversationType, voice events
    Common/                               CompletionEvent hierarchy (TextDelta, ToolCallEvent, ToolResultEvent)
  OpenAgent.ConversationStore.Sqlite/     SQLite persistent store (conversations.db) with schema migration
  OpenAgent.LlmText.OpenAIAzure/         Azure OpenAI Chat Completions provider
  OpenAgent.LlmText.AnthropicSubscription/ Anthropic Messages API via Claude subscription setup-token (OAuth)
  OpenAgent.LlmVoice.OpenAIAzure/        Azure OpenAI Realtime voice provider
  OpenAgent.Tools.FileSystem/             File tools (read, write, append, edit) — scoped to dataPath, UTF-8 no BOM
  OpenAgent.Security.ApiKey/              API key authentication — AddApiKeyAuth() extension
  OpenAgent.Channel.Telegram/             Telegram bot channel — polling/webhook modes, streaming drafts
  OpenAgent.Channel.WhatsApp/             WhatsApp channel — Baileys Node.js bridge, QR pairing, reconnect
    node/                                 Baileys bridge script (baileys-bridge.js) + package.json
  OpenAgent.Tools.Shell/                  Shell exec tool — timeout, process tree kill, merged stdout/stderr
  OpenAgent.Tests/                        Integration tests
src/chat-cli/
  OpenAgent.ChatCli/                      Spectre.Console interactive CLI — uses .env for API key, dev key for localhost
src/web/                                  React frontend — desktop UI with windowed apps
  src/apps/settings/                      Settings app — vertical sidebar, dynamic provider/connection forms
docs/plans/                               Design docs and implementation plans
```

## Architecture Rules

### IAgentLogic is injected context, NOT an orchestrator
IAgentLogic provides system prompt, tools, message history, and tool execution. It is injected INTO LLM providers. Providers call the shots — they call `agentLogic.AddMessage()`, `GetMessages()`, `SystemPrompt`, `Tools`, and `ExecuteToolAsync()`. AgentLogic does not process messages or orchestrate completions.

### Provider pattern
Three provider types, all with IConfigurable:
- **ILlmTextProvider** — single `CompleteAsync` returning `IAsyncEnumerable<CompletionEvent>`. Used by both REST (collected) and WebSocket (streamed). Two implementations: `AzureOpenAiTextProvider` (OpenAI Chat Completions) and `AnthropicSubscriptionTextProvider` (Anthropic Messages API with setup-token OAuth).
- **ILlmVoiceProvider** — creates bidirectional voice sessions
- **IChannelProvider** — inbound channel adapters (Telegram, WhatsApp). `StartAsync`/`StopAsync` lifecycle managed by `ConnectionManager`.

### Lazy provider resolution
All text provider consumers (WebSocket endpoints, channel message handlers) resolve the provider **per message** via `Func<string, ILlmTextProvider>` and read `AgentConfig.TextProvider`/`TextModel` at call time. Provider/model changes take effect without restart. Both providers are registered as keyed singletons — the resolver just picks which one to use.

### Channel provider infrastructure
- **IChannelProviderFactory** — creates providers from `Connection` config. Exposes `Type`, `DisplayName`, `ConfigFields` (for dynamic UI forms), and `SetupStep` (post-creation flow like QR pairing).
- **ConnectionManager** — `IHostedService` that starts enabled connections on startup. Maintains `ConcurrentDictionary<string, IChannelProvider>` of running providers. Matches factories by `connection.Type`.
- **Connection** — persisted in `{dataPath}/config/connections.json` with `Id`, `Name`, `Type`, `Enabled`, `Config` (JsonElement blob).
- **Per-chat conversation mapping** — each platform chat (DM or group) gets its own conversation via derived ID: `{channelType}:{connectionId}:{chatId}`. Uses `GetOrCreate`.
- **Access control** — empty allowlist = allow all (open by default). Restriction is a runtime management concern, not a setup concern.

### CompletionEvent is the universal output type
`CompletionEvent` is an abstract record with three subtypes: `TextDelta`, `ToolCallEvent`, `ToolResultEvent`. Both REST and WebSocket use the same events — REST collects them into a JSON array, WebSocket streams them as individual messages.

### Tool infrastructure
- **ITool** — self-contained tool with definition (JSON Schema) and execution
- **IToolHandler** — groups related tools under a capability domain (e.g. FileSystem, Shell)
- **AgentLogic** — aggregates all tools from registered handlers, routes execution by name
- Tools are NOT part of the system prompt — providers send tool definitions to the LLM via their wire protocol
- Tool calls and results are persisted as Messages with `ToolCalls` and `ToolCallId` fields

### Conversations are created implicitly
No dedicated "create conversation" endpoint. The conversation ID is generated by the client (GUID for app) or derived from the platform chat ID (channel providers). Endpoints use `GetOrCreate` — if the conversation exists, reuse it; if not, create it. Each conversation has:
- **Source** (string) — who initiated it: `"app"`, `"telegram"`, etc.
- **Type** (ConversationType enum) — determines agent behavior and system prompt: `Text`, `Voice`, `Cron`, `WebHook`

### Endpoints are thin
Endpoints validate the request and forward to the provider. No business logic in endpoints.

### Endpoint organization
All endpoints live in `OpenAgent.Api/Endpoints/`. They are ASP.NET Core extension methods on `WebApplication`. Grouped by transport and domain:
- REST endpoints: `ConversationEndpoints`, `ChatEndpoints`, `ConnectionEndpoints`
- WebSocket endpoints: `WebSocketVoiceEndpoints`, `WebSocketTextEndpoints`
- Channel endpoints: `TelegramWebhookEndpoints`, `WhatsAppEndpoints` (QR pairing)

### Authentication
Pluggable auth via extension methods on `IServiceCollection`. Currently `AddApiKeyAuth()` validates `X-Api-Key` header against a configured key. Swap for `AddEntraIdAuth()` when migrating to Entra ID — same shape, different implementation. `/health` is anonymous, all other endpoints require authorization. Dev key in `appsettings.Development.json`, production key via `Authentication__ApiKey` environment variable on Azure.

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
- Inline comments to highlight method flow — make logic scannable at a glance

### Git
- Commit frequently — after each logical change, not in accumulated batches
- Concise commit messages focused on "why"

## Build and Test

```bash
cd src/agent && dotnet build
cd src/agent && dotnet test
```

## CI/CD

GitHub Actions workflow (`.github/workflows/deploy.yml`) builds a Docker image and pushes to `ghcr.io/mbundgaard/open-agent:latest` + `:sha` on every push to master. Azure App Services pull the image independently — restart to pick up a new version. The Dockerfile runs tests during build (`dotnet test`), so broken code never gets pushed as an image.

## Key Design Decisions

- ConversationType drives system prompt selection — the agent behaves differently for voice vs text vs cron
- WebSocket is just transport — which LLM a WebSocket endpoint uses depends on the route, not the protocol
- VoiceSessionManager is pure session lifecycle (create, track, close) — no conversation state updates
- Text provider has a tool call loop with a 10-round safety cap
- SQLite conversation store (conversations.db in dataPath) — persistent across restarts, with schema migration via TryAddColumn
- File tools use UTF-8 without BOM, controlled from a single constant in FileSystemToolHandler
- All tools scoped to `{dataPath}` — no access outside data directory
- Data directory bootstrapped on first startup by `DataDirectoryBootstrap.Run()` — creates required folders (`projects/`, `repos/`, `memory/`, `config/`, `connections/`) and extracts embedded default personality files (AGENTS.md, SOUL.md, IDENTITY.md, USER.md, TOOLS.md, VOICE.md, MEMORY.md, BOOTSTRAP.md) if missing. Also writes empty `config/agent.json` and `config/connections.json`. Never overwrites existing files.
- BOOTSTRAP.md is a first-run conversation ritual — guides the agent through identity discovery with the user, then self-deletes. AGENTS.md checks for its presence on session startup.
- System prompt composed from markdown files in dataPath: AGENTS.md, SOUL.md, IDENTITY.md, USER.md, TOOLS.md, VOICE.md — loaded once at startup, filtered by ConversationType
- BuildChatMessages validates tool call rounds — skips orphaned tool calls to avoid API 400 errors
- WhatsApp uses Baileys (Node.js) as a managed child process — .NET spawns `node baileys-bridge.js`, communicates via stdin/stdout JSON lines. No sidecar container needed.
- WhatsApp auth state (Baileys creds) stored at `{dataPath}/connections/whatsApp/{connectionId}/`
- Node process lifecycle: unpaired (no process) -> pairing (QR) -> connected. LoggedOut clears creds. Reconnect with exponential backoff (2s->30s, 10 max).
- `GET /api/connections/types` returns channel metadata (config fields, setup steps) so the frontend can build dynamic forms without hardcoded channel knowledge
- Settings app uses `IConfigurable` pattern for provider config and `IChannelProviderFactory.ConfigFields` for connection config — both render dynamic forms from backend-provided schemas
- Anthropic setup-token auth requires per-request `Authorization: Bearer` header (not on `DefaultRequestHeaders`), identity headers (`anthropic-beta`, `x-app`, `user-agent`), system prompt as text block array with Claude Code identity prefix, and adaptive thinking for 4.6 models. See [docs/anthropic-setup-token-auth.md](docs/anthropic-setup-token-auth.md).

## Memory

Session-to-session notes. Save memories here in CLAUDE.md — do NOT create separate memory files. Update this section as decisions are made.

### User Preferences
- Prefers design discussions before implementation — brainstorm first, then plan, then build
- Wants to be consulted on naming and architecture, not surprised
- Values small, frequent commits over accumulated batches
- Prefers concise responses — don't over-explain

