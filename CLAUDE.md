# OpenAgent3

Multi-channel AI agent platform. Connects LLM providers (text, voice) to inbound channels (REST API, WebSocket, Telegram, WhatsApp, webhooks) with a shared agent personality layer.

## Project Board

https://github.com/users/mbundgaard/projects/4 — managed via `gh project` and `gh issue` CLI commands. See [docs/project-api.md](docs/project-api.md) for API reference.

Columns: Ideas → Backlog → Roadmap → In Progress → Done. Items within each column are prioritized top-to-bottom.

Labels: `security`, `agent`, `tools`, `skills`, `channels`, `infrastructure`, `ui`, `llm` + `size:S`, `size:M`, `size:L`. All issues should have at least one domain label and a size label.

## Tech Stack

- .NET 10, ASP.NET Core Minimal APIs, System.Text.Json
- Node.js (Baileys bridge for WhatsApp Web protocol)
- React 19, TypeScript, Vite, CSS Modules
- xUnit + WebApplicationFactory for integration tests
- Central Package Management (Directory.Packages.props)

## Project Structure

```
src/agent/
  OpenAgent/                              Host — Program.cs, DI wiring, AgentLogic, VoiceSessionManager, embedded wwwroot extraction
    Installer/                            Windows service install CLI — ServiceInstaller (sc.exe), FirewallRule (netsh), ElevationCheck, EventLogRegistrar, PreInstallChecks, InstallerCli dispatcher
    RootResolver.cs                       Resolves data dir from DATA_DIR env var, falls back to AppContext.BaseDirectory for the Windows service case
  OpenAgent.Api/                          HTTP/WebSocket endpoints (no business logic)
    Endpoints/                            All endpoint files live here
      ConversationEndpoints.cs            List, get, delete conversations
      ChatEndpoints.cs                    Text completion (REST) — returns CompletionEvent JSON array
      WebSocketVoiceEndpoints.cs          Bidirectional voice streaming (WebSocket)
      WebSocketTextEndpoints.cs           Bidirectional text chat (WebSocket) — streams CompletionEvents
  OpenAgent.Contracts/                    Interfaces — IAgentLogic, IConversationStore, ILlmTextProvider, ILlmVoiceProvider, IVoiceSessionManager, ITool, IToolHandler, IOutboundSender
  OpenAgent.Models/                       Shared models — Conversation, Message, ConversationType, voice events
    Common/                               CompletionEvent hierarchy (TextDelta, ToolCallEvent, ToolResultEvent)
  OpenAgent.ConversationStore.Sqlite/     SQLite persistent store (conversations.db) + tool-result blobs, schema migration, compaction core
  OpenAgent.Compaction/                   CompactionSummarizer, CompactionPrompt (Initial/Update), CompactionCutPoint (token-walk, boundary-safe), TokenEstimator
  OpenAgent.LlmText.OpenAIAzure/         Azure OpenAI Chat Completions provider
  OpenAgent.LlmText.AnthropicSubscription/ Anthropic Messages API via Claude subscription setup-token (OAuth)
  OpenAgent.LlmVoice.OpenAIAzure/        Azure OpenAI Realtime voice provider
  OpenAgent.Tools.FileSystem/             File tools (read, write, append, edit) — scoped to dataPath, UTF-8 no BOM
  OpenAgent.Security.ApiKey/              API key authentication — AddApiKeyAuth() extension
  OpenAgent.Channel.Telegram/             Telegram bot channel — polling/webhook modes, streaming drafts, IOutboundSender
  OpenAgent.Channel.WhatsApp/             WhatsApp channel — Baileys Node.js bridge, QR pairing, reconnect, IOutboundSender
    node/                                 Baileys bridge script (baileys-bridge.js) + package.json
  OpenAgent.Channel.Telnyx/               Telnyx phone-call channel — webhook + WebSocket media streaming for inbound voice calls
  OpenAgent.Tools.Shell/                  Shell exec tool — timeout, process tree kill, merged stdout/stderr
  OpenAgent.Skills/                        Agent Skills (agentskills.io spec) — discovery, catalog, activation
  OpenAgent.ScheduledTasks/                Scheduled tasks — cron, interval, one-shot, webhook triggers (feature/scheduled-tasks branch)
  OpenAgent.MemoryIndex/                    Memory index — LLM chunking, hybrid vector+FTS5 search, search_memory + load_memory_chunks tools, hourly hosted service
  OpenAgent.Embedding.Onnx/                 Local embedding provider — multilingual-e5-base via ONNX Runtime
  OpenAgent.Tests/                        Integration tests
src/chat-cli/
  OpenAgent.ChatCli/                      Spectre.Console interactive CLI — uses .env for API key, dev key for localhost
src/web/                                  React frontend — desktop UI with windowed apps
  src/apps/settings/                      Settings app — vertical sidebar, dynamic provider/connection forms
  src/apps/explorer/                      File explorer — browse dataPath, open files
    viewers/                              Format-specific viewers: TextViewer (line numbers), MarkdownViewer (frontmatter + rendered md), JsonlViewer (structured log entries)
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

### Store everything, compute the LLM view
Persistence and LLM context are separate concerns. `IConversationStore.GetMessages(id)` returns raw stored history; the LLM-facing view is built inside each provider's `BuildChatMessages`. Tool results persist full content to `{dataPath}/conversations/{conversationId}/tool-results/{messageId}.txt` (referenced by `Messages.ToolResultRef`) and are loaded on demand via `GetMessages(id, includeToolResultBlobs: true)`. The compaction summary lives on `Conversation.Context`; providers inject it as a `<summary>`-wrapped **user** message so the real system prompt stays stable and cache-friendly. The UI never sees the summary — only post-cut messages. `Messages.Content` always carries the compact tool-result stub as a fallback when the blob is missing.

### Tool infrastructure
- **ITool** — self-contained tool with definition (JSON Schema) and execution
- **IToolHandler** — groups related tools under a capability domain (e.g. FileSystem, Shell)
- **AgentLogic** — aggregates all tools from registered handlers, routes execution by name
- Tools are NOT part of the system prompt — providers send tool definitions to the LLM via their wire protocol
- Tool calls and results are persisted as Messages with `ToolCalls` and `ToolCallId` fields

### Agent Skills (agentskills.io specification)
Skills are markdown instruction documents (`SKILL.md`) that teach the agent specialized workflows. Implements the open [Agent Skills](https://agentskills.io/specification) format for cross-client compatibility.
- **Discovery** — `SkillDiscovery` scans `{dataPath}/skills/*/SKILL.md` at startup
- **Catalog** — `SkillCatalog` builds `<available_skills>` XML injected into the system prompt (~50-100 tokens per skill)
- **Persistent activation** — `activate_skill` stores skill name on the `Conversation.ActiveSkills` list. `SystemPromptBuilder.Build` appends active skill bodies to the system prompt — compaction-proof.
- **Four tools** — `activate_skill`, `deactivate_skill`, `list_active_skills`, `activate_skill_resource`
- **Resource loading** — `activate_skill_resource(skill_name, path)` loads files relative to the skill directory. Tool results are ephemeral and safe to strip during compaction.
- **Progressive disclosure** — catalog (always), skill body (when activated, in system prompt), resources (on demand, in tool results)
- Skills are NOT tools — they teach the agent HOW to use existing tools for specific workflows

### Conversations are created implicitly
No dedicated "create conversation" endpoint. The conversation ID is generated by the client (GUID for app) or derived from the platform chat ID (channel providers). Endpoints use `GetOrCreate` — if the conversation exists, reuse it; if not, create it. Each conversation has:
- **Source** (string) — who initiated it: `"app"`, `"telegram"`, etc.
- **Type** (ConversationType enum) — determines agent behavior and system prompt: `Text`, `Voice`, `ScheduledTask`, `WebHook`

### Endpoints are thin
Endpoints validate the request and forward to the provider. No business logic in endpoints.

### Endpoint organization
All endpoints live in `OpenAgent.Api/Endpoints/`. They are ASP.NET Core extension methods on `WebApplication`. Grouped by transport and domain:
- REST endpoints: `ConversationEndpoints`, `ChatEndpoints`, `ConnectionEndpoints`, `ScheduledTaskEndpoints`, `LogEndpoints`, `FileExplorerEndpoints`, `ToolEndpoints`
- WebSocket endpoints: `WebSocketVoiceEndpoints`, `WebSocketTextEndpoints`
- Channel endpoints: `TelegramWebhookEndpoints`, `WhatsAppEndpoints` (QR pairing)

### API Reference

All endpoints require `X-Api-Key` header except `/health`.

#### Conversations
| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/conversations` | List all conversations |
| `GET` | `/api/conversations/{conversationId}` | Get conversation with messages |
| `DELETE` | `/api/conversations/{conversationId}` | Delete conversation (cascades to `tool-results/` blobs) |
| `PATCH` | `/api/conversations/{conversationId}` | Update writable fields (`source`, `provider`, `model`, `channel_chat_id`, `intention`, `mention_filter`). Field omitted → unchanged. Empty string / empty array → clear. |
| `POST` | `/api/conversations/{conversationId}/compact` | Manual compaction trigger, optional body `{"instructions": "..."}` — returns `{ compacted: bool }` |

#### Chat (text completion)
| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/conversations/{conversationId}/messages` | Send message, returns CompletionEvent JSON array |

#### Scheduled Tasks
| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/scheduled-tasks` | List all tasks |
| `GET` | `/api/scheduled-tasks/{taskId}` | Get single task with state |
| `POST` | `/api/scheduled-tasks` | Create task |
| `PUT` | `/api/scheduled-tasks/{taskId}` | Update task |
| `DELETE` | `/api/scheduled-tasks/{taskId}` | Delete task |
| `POST` | `/api/scheduled-tasks/{taskId}/run` | Execute immediately |
| `POST` | `/api/scheduled-tasks/{taskId}/trigger` | Trigger with optional webhook context body |

#### Tools
| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/tools` | List all tools with definitions (name, description, parameters schema) |
| `GET` | `/api/tools/{toolName}` | Get single tool definition |
| `POST` | `/api/tools/{toolName}/execute` | Execute a tool directly, returns result + duration |

#### Memory Index
| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/memory-index/run` | Trigger an indexing run immediately, returns `IndexResult` |
| `GET` | `/api/memory-index/stats` | Aggregate counts: totalChunks, totalDays, oldestDate, newestDate |

#### Logs
| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/logs` | List log files (filename, date, sizeBytes, lineCount) |
| `GET` | `/api/logs/{filename}` | Read log lines with paging and filtering |

**Log query parameters** (all optional, combinable):
- `offset` / `limit` — line-based paging (default: offset=0, limit=200). Lines are oldest-first.
- `level` — comma-separated filter: `ERR,WRN` or `Error,Warning`. Accepts abbreviations (VRB, DBG, INF, WRN, ERR, FTL) or full Serilog names.
- `since` / `until` — ISO 8601 timestamp range filter on `@t` field.
- `search` — case-insensitive substring match on the raw JSON line.
- `tail` — return last N matched entries (applied before offset/limit).

**Log response:** `{ filename, totalLines, matchedLines, offset, limit, lines[] }`. Each line is a raw JSON string (Serilog compact format: `@t`=timestamp, `@l`=level, `@m`=rendered message, `@mt`=template).

#### Files
| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/files?path={dir}` | List directory contents under dataPath |
| `GET` | `/api/files/content?path={file}` | Read file contents as text |

#### Connections
| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/connections` | List all channel connections |
| `GET` | `/api/connections/types` | Channel metadata (config fields, setup steps) |
| `POST` | `/api/connections` | Create connection |
| `PUT` | `/api/connections/{connectionId}` | Update connection |
| `DELETE` | `/api/connections/{connectionId}` | Delete connection |
| `POST` | `/api/connections/{connectionId}/start` | Start connection |
| `POST` | `/api/connections/{connectionId}/stop` | Stop connection |

#### Webhooks
| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/webhook/conversation/{conversationId}` | Anonymous. Push body (plain text, any `Content-Type`) as a user message into an existing conversation; agent processes asynchronously. Returns `202`. `404` if conversation does not exist, `400` if body empty. |

### Authentication
Pluggable auth via extension methods on `IServiceCollection`. Currently `AddApiKeyAuth(string apiKey)` validates `X-Api-Key` header against the resolved key. Swap for `AddEntraIdAuth()` when migrating to Entra ID — same shape, different implementation. `/health` is anonymous, all other endpoints require authorization.

`ApiKeyResolver.Resolve({dataPath}, IConfiguration)` decides the active key at startup, in this order:
1. `Authentication:ApiKey` config value (env var `Authentication__ApiKey`, `appsettings.Development.json`, command-line). When set, it's also persisted back to `{dataPath}/config/agent.json`'s `apiKey` field so the file is the source of truth at rest.
2. Existing `apiKey` string in `agent.json`.
3. Generate a 24-byte hex key, persist it.

Program.cs prints the bound URL(s) on startup with the key as a hash fragment — `http://localhost:8080/#token=<apiKey>` — which the React app reads via `window.location.hash` (see `src/web/src/auth/token.ts`). Ctrl-click the URL to open the UI pre-authenticated.

### Windows service deployment
Same codebase, Windows-specific deployment target. Run `OpenAgent.exe` with no args for console mode, or use the installer verbs:
- `--install` registers a Windows service that runs the exe **in place** (no copy). From an elevated CMD, extract the published folder to e.g. `C:\OpenAgent\`, `cd` in, run `--install`. Service registered with `sc.exe`, account `LocalSystem`, start type auto, `sc failure` recovery (restart/5s/5s/60s, reset 24h). See `src/agent/OpenAgent/Installer/InstallerCli.cs`.
- `--uninstall` stops + deletes the service, removes firewall rule. Data (`config/`, `logs/`, `conversations.db`, symlinks) preserved.
- `--restart` stop + start.
- `--status` print installed/running state.
- `--service` is the SCM-invoked entry point. Binds `UseWindowsService()` and adds the `EventLog` logging provider (source `OpenAgent`, registered by `EventLogRegistrar`).

Pre-install checks (`PreInstallChecks`): `node\baileys-bridge.js` next to the exe, `node --version` exits 0, install path has no null/newline chars. Admin gated via `ElevationCheck.IsAdministrator()` (`WindowsPrincipal.IsInRole(Administrator)`).

Upgrade flow (self-hosted shape): stop service, replace files, start service. Because Windows locks the running exe, you can't overwrite it in place while the service is up.

**Symlinks** for reaching paths outside the data dir (e.g. `D:\Media`, `E:\Downloads`) are declared in `config/agent.json` under `symlinks`: `{ "media": "D:\\Media", ... }`. `DataDirectoryBootstrap` creates directory junctions on Windows (`cmd /c mklink /J`) or symlinks on Linux via `IDirectoryLinkCreator`. Junctions need no admin/Developer Mode on Windows and remain transparent to the single-root file tool gate. Symlink changes require `--restart` to take effect.

**Publish script:** `scripts/publish-windows.ps1` runs `npm run build` in `src/web`, zips `dist/` into `src/agent/OpenAgent/wwwroot.zip` (gitignored, embedded as assembly resource), then `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`. Output: `publish/win-x64/{OpenAgent.exe, node/, onnxruntime*.lib}`. The Baileys bridge node_modules is not shipped by publish — install with `npm ci --omit=dev` in the published `node/` folder after copying (or handle in your own install flow).

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
- Separate features get separate commits, then push once
- Concise commit messages focused on "why"

### Skills
- Skills are instructions, not tooling — they teach the agent how to use existing tools (shell_exec, file_read, etc.)
- No wrapper scripts (.sh, .py) — the agent calls curl/jq directly via shell_exec
- Skill config (API keys, credentials) lives in `{dataPath}/config/{name}.json`
- Working data goes in `{dataPath}/skills/{skill-name}/data/` (co-located with the skill)
- API specs should be inline in SKILL.md — don't use progressive disclosure for small files (<10KB)
- Separate GET response schemas from POST/PUT request schemas — response-only fields (index, id, timestamps) must not appear in request examples

## Build and Test

```bash
cd src/agent && dotnet build
cd src/agent && dotnet test
```

Windows service distribution (runs the React build + self-contained publish):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-windows.ps1
# output: publish/win-x64/{OpenAgent.exe, node/, onnxruntime*.lib}
```

- Windows: use `python` not `python3` (python3 is not aliased on this machine)
- Integration tests seed `{dataPath}/config/agent.json` via `TestSetup.EnsureConfigSeeded()` and export `DATA_DIR` so the test host and the seed agree on the path (RootResolver's fallback would otherwise diverge).

## CI/CD

GitHub Actions workflow (`.github/workflows/deploy.yml`) builds a Docker image and pushes to `ghcr.io/mbundgaard/open-agent:latest` + `:sha` on every push to master. Azure App Services pull the image independently — restart to pick up a new version. The Dockerfile runs tests during build (`dotnet test`), so broken code never gets pushed as an image.

## Key Design Decisions

- ConversationType drives system prompt selection — the agent behaves differently for voice vs text vs scheduledtask
- WebSocket is just transport — which LLM a WebSocket endpoint uses depends on the route, not the protocol
- VoiceSessionManager is pure session lifecycle (create, track, close) — no conversation state updates
- Text provider has a tool call loop with a 10-round safety cap
- SQLite conversation store (conversations.db in dataPath) — persistent across restarts, with schema migration via TryAddColumn
- File tools use UTF-8 without BOM, controlled from a single constant in FileSystemToolHandler
- All tools scoped to `{dataPath}` — no access outside data directory
- Data directory bootstrapped on first startup by `DataDirectoryBootstrap.Run()` — creates required folders (`repos/`, `memory/`, `config/`, `connections/`, `skills/`) and extracts embedded default personality files (AGENTS.md, SOUL.md, IDENTITY.md, USER.md, TOOLS.md, VOICE.md, MEMORY.md, BOOTSTRAP.md) if missing. Also writes empty `config/agent.json` and `config/connections.json`. Never overwrites existing files.
- BOOTSTRAP.md is a first-run conversation ritual — guides the agent through identity discovery with the user, then self-deletes. AGENTS.md checks for its presence on session startup.
- System prompt composed from markdown files in dataPath: AGENTS.md, SOUL.md, IDENTITY.md, USER.md, TOOLS.md, VOICE.md — loaded once at startup, filtered by ConversationType
- System prompt includes current time in Europe/Copenhagen timezone with weekday and ISO week number (e.g. `Saturday 2026-04-11T17:10 Europe/Copenhagen (UTC+2), week 15`). Hardcoded timezone — does not rely on OS locale.
- BuildChatMessages validates tool call rounds — skips orphaned tool calls to avoid API 400 errors
- WhatsApp uses Baileys (Node.js) as a managed child process — .NET spawns `node baileys-bridge.js`, communicates via stdin/stdout JSON lines. No sidecar container needed.
- WhatsApp auth state (Baileys creds) stored at `{dataPath}/connections/whatsApp/{connectionId}/`
- Node process lifecycle: unpaired (no process) -> pairing (QR) -> connected. LoggedOut clears creds. Reconnect with exponential backoff (2s->30s, 10 max).
- `GET /api/connections/types` returns channel metadata (config fields, setup steps) so the frontend can build dynamic forms without hardcoded channel knowledge
- Settings app uses `IConfigurable` pattern for provider config and `IChannelProviderFactory.ConfigFields` for connection config — both render dynamic forms from backend-provided schemas
- Anthropic setup-token auth requires per-request `Authorization: Bearer` header (not on `DefaultRequestHeaders`), identity headers (`anthropic-beta`, `x-app`, `user-agent`), system prompt as text block array with Claude Code identity prefix, and adaptive thinking for 4.6 models. See [docs/anthropic-setup-token-auth.md](docs/anthropic-setup-token-auth.md).
- Skills follow the agentskills.io open spec — YAML frontmatter (name, description required) + markdown body. Compatible with Claude Code, Cursor, VS Code Copilot, and 30+ other clients.
- Active skills are persisted on Conversation.ActiveSkills (JSON column in SQLite) and injected into the system prompt — compaction never removes them.
- Skill resources loaded via activate_skill_resource are ephemeral tool results — the compactor can strip them safely, the agent re-requests if needed.
- GetSystemPrompt takes activeSkills parameter so the system prompt is per-conversation, not just per-type.
- ITool.ExecuteAsync takes conversationId — providers already pass it through AgentLogic. Existing tools ignore it; skill tools use it to modify conversation state.
- Telegram webhook mode does NOT delete the webhook on stop — avoids message loss during container restarts. `StartAsync` always re-registers (idempotent).
- IOutboundSender interface enables proactive messaging — channel providers that support outbound implement it. Used by scheduled tasks for delivery.
- File explorer reads with `FileShare.ReadWrite` so Serilog-locked log files can be opened.
- Log files (Serilog compact JSON) stored at `{dataPath}/logs/log-{date}.jsonl` with daily rolling. Queryable via `/api/logs` endpoints with level, time range, search, and tail filters.
- Data directory resolution: `DATA_DIR` env var wins (Docker / Azure set it to `/home/data`); when unset, `AppContext.BaseDirectory` (the folder next to the running exe). The Linux Docker deployment sees no change; the Windows service falls through to exe-relative without needing env config.
- React UI is embedded in the assembly as `OpenAgent.wwwroot.zip` and extracted to `{exe-dir}/wwwroot/` on every startup by a helper at the top of `Program.cs` (ran on Windows + Linux, same code path). Dockerfile zips the React build in its `web-build` stage, copies the zip into the .NET source tree, and `dotnet publish` embeds it.
- `<StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>` on `OpenAgent.csproj` — the manifest-based system assumes a physical `wwwroot/` path at build time, which doesn't exist in source (we use zip-embedding). The plain `UseStaticFiles` + ContentRoot/wwwroot serves the extracted files fine.
- Host defaults live in `Program.cs`, not `appsettings.json`: Kestrel binds to `http://localhost:8080` (overridable via `ASPNETCORE_URLS`), log filters quiet ASP.NET Core info chatter while keeping `Microsoft.Hosting.Lifetime` at Information so "Now listening on:" shows on startup. `appsettings.json` is gone from the published exe; an optional one next to the exe still wins if present.

## Memory

Session-to-session notes. Save memories here in CLAUDE.md — do NOT create separate memory files. Update this section as decisions are made.

### WebFetch SSRF Protection (Issue #7 — Closed)
- IPv6: allows public addresses, blocks loopback (::1), link-local (fe80::/10), ULA (fc00::/7)
- IPv4: blocks RFC 1918, loopback, link-local, CGN (100.64.0.0/10), benchmark (198.18.0.0/15)
- HttpClient timeout: 30s (was default 100s)
- DNS rebinding (TOCTOU): accepted risk — would require `ConnectCallback` rewrite, low value for auth-gated agent

### Code Review
- Full codebase review (11 domains) in [docs/review/code-review-prompts.md](docs/review/code-review-prompts.md)
- Findings: [docs/review/review-by-opus-high.md](docs/review/review-by-opus-high.md) (17 high-severity), [docs/review/review-by-opus.md](docs/review/review-by-opus.md) (full)
- All findings tracked as GitHub issues with fix instructions in the description

### Memory System Design
- Three-job architecture: Index → Digest → Background. See [docs/memory/DESIGN.md](docs/memory/DESIGN.md)
- Issues: #17 (Index), #19 (Digest), #51 (Background) — must be built in order
- **Index (done):** `OpenAgent.MemoryIndex` + per-family embedding projects. Hourly hosted service scans past-window memory files, LLM-chunks them into topics (one call per file, `discard` outcome supported), embeds each chunk via the configured `IEmbeddingProvider`, persists to `memory_chunks` + FTS5. Hybrid search (0.7 cosine + 0.3 BM25) exposed via `search_memory` / `load_memory_chunks` tools and `/api/memory-index/{run,stats}` endpoints.
- **Memory file lifecycle.** Agent writes daily notes to `{dataPath}/memory/{YYYY-MM-DD}.md`. `SystemPromptBuilder` loads every file currently in `memory/` root (newest first) into the prompt. The indexer picks up everything past the `AgentConfig.MemoryDays` window (top-N by filename), chunks + embeds + stores them, then MOVES the source file to `memory/backup/` (not delete). `backup/` is not scanned by the prompt loader, so indexed files leave the prompt automatically and become reachable only via `search_memory`. Discarded files (LLM `discard: true`) are deleted outright. `MemoryDays` is the indexer's threshold only — the prompt loader ignores it.
- **Embedding providers: one project per model family.** Current: `OpenAgent.Embedding.OnnxMultilingualE5` (XLM-R Unigram SentencePiece, `ProviderKey = "multilingual-e5"`), `OpenAgent.Embedding.OnnxBge` (BERT WordPiece, `ProviderKey = "bge"`). Each provider exposes `Key`, `Model`, `Dimensions`, reads its model from `AgentConfig.EmbeddingModel`, and auto-downloads from HuggingFace into `{dataPath}/models/{model}/` on first use (in-memory `SemaphoreSlim` + atomic temp-file rename). No shared base class — copying the nearest sibling is the intended way to add a new family.
- **Tool descriptions prescribe when to call, not just what the tool does.** Observed: LLMs ignore tools whose `Description` reads like reference docs. The description is the decision prompt. See `SearchMemoryTool` for a worked example (explicit "call this BEFORE saying you don't remember" + trigger conditions).
- Each system job today is its own `IHostedService`. Once #19 lands we should extract a small `ISystemJob` / `SystemJobRunner` abstraction so all three jobs share one tick loop and admin visibility — deferred until the second instance.

### Context Management Rewrite (shipped 2026-04-24)
Three-PR rewrite landed on master: tool-result blob storage, token-aware boundary-safe cut points, per-model context window, iterative compaction prompts, three triggers (threshold/overflow/manual). Design doc: [docs/plans/2026-04-24-context-management-rewrite.md](docs/plans/2026-04-24-context-management-rewrite.md). Key behaviors:
- **Three compaction triggers** all route through `SqliteConversationStore.PerformCompactionAsync(id, reason, customInstructions?, ct)`. Reasons: `Threshold` (post-turn background, fires when `LastPromptTokens >= ContextWindowTokens * CompactionTriggerPercent / 100`), `Overflow` (providers catch context-length errors, call `IAgentLogic.CompactAsync`, retry the turn **once**; second overflow surfaces as error), `Manual` (`POST /api/conversations/{id}/compact`).
- **Cut point algorithm** (`CompactionCutPoint.Find`): walks messages newest → oldest accumulating estimated tokens until `KeepRecentTokens` budget is reached, then snaps to the nearest earlier `user` or `assistant`-without-tool-calls boundary. Never splits a tool-call/tool-result pair.
- **Iterative summaries.** `CompactionPrompt.Initial` runs on the first compaction; `CompactionPrompt.Update` wraps the prior `Conversation.Context` in `<previous-summary>` tags and merges new messages into it. Both prompts require preserving the *content* of `search_memory` / `load_memory_chunks` results so the post-cut agent doesn't re-search.
- **Per-conversation cancellation.** `ConcurrentDictionary<string, CancellationTokenSource>` in the store; `Delete(conversationId)` and store `Dispose()` both cancel in-flight compactions. Cutoff-swap guarded by a snapshot of `LastRowId` so concurrent writes during summarization aren't absorbed.
- **Observability.** Structured Serilog events: `compaction.start` / `compaction.complete` (with `messagesCompacted`, `tokensBefore`, `durationMs`) / `compaction.error` / `compaction.cancelled`. Query via `/api/logs?search=compaction`.
- **Disabled compaction is graceful.** When `AgentConfig.CompactionProvider` or `CompactionModel` is unset, `CompactionSummarizer` throws `CompactionDisabledException` once (warn-logged once per startup) and all callers fall through returning `false` — no error loops.
- **CI flake fix (2026-04-25).** xUnit's `IClassFixture<WebApplicationFactory<Program>>` cleanup occasionally threw `ObjectDisposedException` on Linux/Docker CI — varying class (HealthEndpointTests, VoiceWebSocketTests, ...), all 308+ tests passing first. Root cause: `SqliteConversationStore.TryStartCompaction` fires-and-forgets a `Task.Run` for threshold compactions; on Linux the WebApplicationFactory teardown completed faster than that background task, so the late tick ran against a logger / DI container that was already disposed. Fix: track the background task in a `ConcurrentDictionary<Task, byte>`, set a `_disposed` flag, wait (bounded 2s) for tracked tasks in `Dispose`, and bail out early in the background lambda if disposed. Also hardened `MemoryIndexHostedService.LoopAsync` (catches OperationCanceled + ObjectDisposed) and stopped disposing the SemaphoreSlim in `ScheduledTaskService.Dispose` (in-flight WaitAsync would have surfaced the same race). Dockerfile `dotnet test` now passes `-l "console;verbosity=detailed"` so any future class-cleanup failure prints a full stack instead of just the exception type. Regression test: `SqliteConversationStoreTests.Dispose_waits_for_in_flight_threshold_compaction_so_logger_isnt_torn_down_under_it`.

### Mention Filter (shipped 2026-04-25)
Per-conversation list `Conversation.MentionFilter : List<string>?`. When non-empty, inbound user messages whose text does not contain any listed name (case-insensitive substring) are silently dropped before any side effect. Spec: [docs/superpowers/specs/2026-04-25-conversation-mention-filter-design.md](docs/superpowers/specs/2026-04-25-conversation-mention-filter-design.md).
- **Helper:** `MentionMatcher.ShouldAccept(Conversation, string)` in `OpenAgent.Models.Conversations`. Pure predicate, no side effects.
- **Storage:** `MentionFilter TEXT` column on `Conversations`, JSON-array hydration mirroring `ActiveSkills`. `null` and `[]` both persist as `NULL` (no semantic difference).
- **Entry points gated:** REST chat (`ChatEndpoints`), webhook push (`WebhookEndpoints`), Telegram (`TelegramMessageHandler`), WhatsApp (`WhatsAppMessageHandler`). All four load the `Conversation` first, then the filter, then any side effect (typing/composing indicator, LLM call). One `LogDebug` line per drop, no other trace.
- **API:** `PATCH /api/conversations/{id}` accepts `mention_filter` with the same null/empty/non-empty semantics as `intention` (omitted → unchanged, `[]` → clear, non-empty → replace). Surfaced on `ConversationListItemResponse` and `ConversationDetailResponse`.
- **LLM tools:** `set_mention_filter(names: string[])` and `clear_mention_filter()` so the agent can toggle gating on its own conversation. Lives next to `set_intention` / `clear_intention` in `OpenAgent.Tools.Conversation`. Set-tool trims, drops empty entries, caps at 20 names × 50 chars; rejects empty post-normalization with an error pointing at the clear tool.
- **Channel handler reorder:** Telegram and WhatsApp handlers were restructured so `FindOrCreateChannelConversation` runs *before* the typing/composing indicator. The filter sits between them — first-message-ever paths still write the conversation row (needed to read the filter), but no indicator fires on a dropped message.
- **Out of scope (deferred):** native reply-to-bot detection, word-boundary matching, UI editor, auto-population from bot username on group join.

### Reply Quoting (shipped 2026-04-25, refined 2026-04-26)
When a user replies to a specific earlier message on Telegram or WhatsApp, the LLM sees the quoted text inline as an XML-tagged block so it can disambiguate which message is being replied to. Plans: [docs/superpowers/plans/2026-04-25-reply-quoting.md](docs/superpowers/plans/2026-04-25-reply-quoting.md), [docs/superpowers/plans/2026-04-25-whatsapp-stanza-id.md](docs/superpowers/plans/2026-04-25-whatsapp-stanza-id.md).
- **Format:** `<replying_to author="user|assistant" timestamp="2026-04-26T06:53:30+00:00">\n{content}\n</replying_to>\n\n{user reply}`. Body is whitespace-collapsed and truncated at 200 chars + `…`. Lives in `OpenAgent.Models.Common.ReplyQuoteFormatter`. Output is **never persisted** — built at LLM-context-build time only.
- **Why XML over markdown blockquote.** First shipped as `> {quoted}\n\n` but Claude in production reliably failed to interpret the prefix as reply context (assistant kept saying "I can't see reply context"). Verified via prod logs. Switching to `<replying_to>` fixed it on the next deploy — Anthropic's training reinforces XML-tagged structured input as load-bearing semantics rather than formatting noise.
- **Render path.** Both `AzureOpenAiTextProvider.BuildChatMessages` and `AnthropicSubscriptionTextProvider.BuildMessages` build a single-pass `Dictionary<string, Message>` keyed by `ChannelMessageId` from `storedMessages`. When emitting a user/assistant message that has `ReplyToChannelMessageId` set, the provider `TryGetValue`s the dictionary and calls `ReplyQuoteFormatter.Format(msg.Content, quotedMessage)`. Lookup miss (replied-to message compacted out, etc.) → falls through to plain content.
- **Inbound capture.** Telegram captures `Update.Message.ReplyToMessage.MessageId` natively. WhatsApp's Baileys bridge extracts `extendedTextMessage.contextInfo.stanzaId` and emits it as `replyTo` on the message event; .NET sets `Message.ReplyToChannelMessageId` from it.
- **WhatsApp stanza-ID round-trip.** To make reply-to-bot lookups work on WhatsApp, the assistant message's `ChannelMessageId` must be the real Baileys stanza ID, not a synthetic placeholder. The bridge protocol gained a `sent` event echoed after each `send` command, carrying the resulting stanza ID (success) or error message. Each send uses a per-call `correlationId` (GUID) so out-of-order responses (e.g. after a 30s timeout) match the right caller via `ConcurrentDictionary<string, TaskCompletionSource<string?>>`. `WhatsAppNodeProcess.SendTextAndWaitAsync` returns the real stanza ID; `WhatsAppMessageHandler` back-fills `assistantMessage.ChannelMessageId` with it (mirrors the Telegram pattern at `TelegramMessageHandler.cs:511-533`).
- **Out of scope (deferred):** agent-driven outbound replies (either auto-threading bot responses to inbound user messages, or LLM picking the reply target via inline marker). Telegram's `replyParameters.message_id` and WhatsApp's `sock.sendMessage`'s `quoted` parameter would carry it; not yet wired. The proactive `WhatsAppChannelProvider.SendMessageAsync` path also still uses a fire-and-forget GUID and produces a benign `WARN: unknown correlationId` log per send — also deferred.

### User Preferences
- Prefers design discussions before implementation — brainstorm first, then plan, then build
- Wants to be consulted on naming and architecture, not surprised
- Values small, frequent commits over accumulated batches
- Prefers concise responses — don't over-explain

