# OpenAgent Architecture

Multi-channel AI agent platform. Connects LLM providers (text, voice) to inbound/outbound channels (REST, WebSocket, Telegram, WhatsApp) with a shared conversation and agent personality layer.

---

## Tech Stack

- **.NET 10**, ASP.NET Core Minimal APIs, System.Text.Json
- **SQLite** (conversations + messages), JSON files (config, connections, scheduled tasks)
- **Node.js** — Baileys bridge for WhatsApp Web protocol (child process, JSON lines over stdin/stdout)
- **React 19**, TypeScript, Vite, CSS Modules — desktop-like windowed UI
- **Serilog** — structured JSONL logs with daily rolling, runtime-configurable levels
- **xUnit** + WebApplicationFactory for integration tests

---

## Project Structure

```
src/agent/
  OpenAgent/                              Host — Program.cs, DI wiring, AgentLogic, VoiceSessionManager
  OpenAgent.Api/                          HTTP/WebSocket endpoints (no business logic)
    Endpoints/                            All endpoint files
  OpenAgent.Contracts/                    All interfaces (IAgentLogic, ILlmTextProvider, ITool, etc.)
  OpenAgent.Models/                       Shared models — Conversation, Message, CompletionEvent, etc.
  OpenAgent.ConversationStore.Sqlite/     SQLite persistent store with incremental schema migration
  OpenAgent.LlmText.OpenAIAzure/          Azure OpenAI Chat Completions provider (streaming SSE)
  OpenAgent.LlmText.AnthropicSubscription/ Anthropic Messages API via Claude subscription OAuth token
  OpenAgent.LlmVoice.OpenAIAzure/         Azure OpenAI Realtime voice (WebSocket)
  OpenAgent.Tools.FileSystem/             file_read, file_write, file_edit, file_append — scoped to dataPath
  OpenAgent.Tools.Shell/                  shell_exec — timeout, process tree kill, tail-truncated output
  OpenAgent.Security.ApiKey/              X-Api-Key header auth (+ query string fallback for WebSocket)
  OpenAgent.Channel.Telegram/             Telegram bot — polling or webhook, streaming drafts, IOutboundSender
  OpenAgent.Channel.WhatsApp/             WhatsApp via Baileys Node.js bridge, QR pairing, reconnect
    node/                                 baileys-bridge.js + package.json
  OpenAgent.Skills/                       Agent Skills (agentskills.io spec) — discovery, catalog, activation
  OpenAgent.ScheduledTasks/               Cron/interval/one-shot tasks with delivery to channels
  OpenAgent.Compaction/                   Token-threshold compaction — summarizes old messages, saves memories
  OpenAgent.Tests/                        Integration tests

src/web/                                  React SPA — windowed desktop UI
  src/apps/settings/                      Provider config, system prompt editor, connections
  src/apps/explorer/                      File browser with text/markdown/JSONL viewers

src/chat-cli/
  OpenAgent.ChatCli/                      Spectre.Console interactive CLI (WebSocket or REST)
```

---

## Request Flow (Text)

1. Message arrives via REST endpoint, WebSocket, or channel (Telegram/WhatsApp)
2. Conversation looked up or created (`GetOrCreate` / `FindOrCreateChannelConversation`)
3. Text provider resolved by key from `AgentConfig.TextProvider`
4. `ILlmTextProvider.CompleteAsync(conversation, userMessage)` called — yields `CompletionEvent` stream
5. Provider builds system prompt + full message history + tool definitions, POSTs to LLM API with streaming
6. **Tool call loop** (up to 10 rounds):
   - LLM returns `finish_reason = "tool_calls"` → accumulate tool call fragments
   - Execute each via `IAgentLogic.ExecuteToolAsync(conversationId, name, arguments)`
   - Persist assistant message + tool result messages
   - Yield `ToolCallEvent` + `ToolResultEvent` to caller
   - Loop: re-call LLM with tool results in request history
7. Final assistant message persisted; `AssistantMessageSaved(messageId)` yielded
8. REST: events collected into JSON array response; WebSocket: events streamed as individual messages
9. Channel providers send response to external platform (Telegram message, WhatsApp text)

---

## Key Abstractions

### IAgentLogic

Injected into LLM providers. Providers call it — it does not orchestrate.

```csharp
GetSystemPrompt(source, type, activeSkills) → string
Tools → IReadOnlyList<AgentToolDefinition>
ExecuteToolAsync(conversationId, name, arguments, ct) → Task<string>
AddMessage(conversationId, message)
GetMessages(conversationId) → IReadOnlyList<Message>
GetConversation(conversationId) → Conversation?
UpdateConversation(conversation)
```

`AgentLogic` flattens tools from all registered `IToolHandler` instances into a single list and routes execution by tool name.

### ILlmTextProvider : IConfigurable

```csharp
// Full conversation context — builds system prompt, tools, history; persists messages
CompleteAsync(conversation, userMessage, ct) → IAsyncEnumerable<CompletionEvent>

// Raw completion — no context, tools, or persistence; used by compaction and expand tool
CompleteAsync(messages, model, options, ct) → IAsyncEnumerable<CompletionEvent>
```

Two implementations:
- **AzureOpenAiTextProvider** — Azure OpenAI Chat Completions, SSE streaming, tool call loop, token tracking
- **AnthropicSubscriptionTextProvider** — Anthropic Messages API via OAuth setup-token, adaptive thinking for claude-4-6 models, per-request `Authorization: Bearer` header, system prompt as text block array with Claude Code identity prefix

Both registered as keyed singletons. Resolved per-message via `Func<string, ILlmTextProvider>` reading `AgentConfig.TextProvider` at call time — provider/model changes take effect without restart.

### ILlmVoiceProvider : IConfigurable

```csharp
StartSessionAsync(conversation, ct) → Task<IVoiceSession>
```

`IVoiceSession` — bidirectional audio streaming:
- `SendAudioAsync(pcm16)`, `CommitAudioAsync()` — ingest audio frames
- `ReceiveEventsAsync()` → yields `VoiceEvent` subtypes: `AudioDelta`, `TranscriptDelta`, `SpeechStarted`, `SessionError`, etc.
- `CancelResponseAsync()` — interrupt assistant

Implementation: `AzureOpenAiRealtimeVoiceProvider` — WSS to Azure OpenAI Realtime API, server VAD, function call loop via `response.function_call_arguments.done` events.

### IConfigurable

All providers and stores implement this:

```csharp
string Key { get; }
IReadOnlyList<ProviderConfigField> ConfigFields { get; }
void Configure(JsonElement configuration);
IReadOnlyList<string> Models => [];
```

At startup, `Program.cs` loads each configurable's JSON config from `IConfigStore` and calls `Configure()`. Admin API endpoints allow runtime updates without restart.

### CompletionEvent Hierarchy

```csharp
abstract record CompletionEvent
record TextDelta(string Content) : CompletionEvent
record ToolCallEvent(string ToolCallId, string Name, string Arguments) : CompletionEvent
record ToolResultEvent(string ToolCallId, string Name, string Result) : CompletionEvent
record AssistantMessageSaved(string MessageId) : CompletionEvent
```

REST collects into a JSON array; WebSocket streams each event individually.

### ITool / IToolHandler

```csharp
// ITool
AgentToolDefinition Definition { get; }   // name, description, JSON Schema params
Task<string> ExecuteAsync(string arguments, string conversationId, ct)

// IToolHandler
IReadOnlyList<ITool> Tools { get; }
```

Handlers: `FileSystemToolHandler`, `ShellToolHandler`, `WebFetchToolHandler`, `ExpandToolHandler`, `SkillToolHandler`, `ScheduledTaskToolHandler`. All registered as `IToolHandler` in DI; `AgentLogic` flattens them.

### IChannelProvider / IChannelProviderFactory / IOutboundSender

```csharp
// IChannelProvider
StartAsync(ct), StopAsync(ct)

// IChannelProviderFactory
string Type, DisplayName
IReadOnlyList<ProviderConfigField> ConfigFields
ChannelSetupStep? SetupStep    // e.g. QR code for WhatsApp
IChannelProvider Create(connection)

// IOutboundSender (opt-in for proactive messaging)
Task SendTextAsync(chatId, text)
```

`ConnectionManager` (IHostedService) holds a `ConcurrentDictionary<string, IChannelProvider>` of running providers, starts/stops them by looking up the matching factory by `connection.Type`.

---

## Data Models

### Conversation

```
Id (GUID), Source ("app"/"telegram"/...), Type (Text|Voice)
Provider, Model
CreatedAt, LastActivity
VoiceSessionId, VoiceSessionOpen
LastPromptTokens, TotalPromptTokens, TotalCompletionTokens, TurnCount
ActiveSkills (List<string>, JSON in SQLite)
Context (compaction summary), CompactedUpToRowId, CompactionRunning
ChannelType, ConnectionId, ChannelChatId   ← channel binding tuple
```

### Message

```
Id, ConversationId
Role ("user"/"assistant"/"tool"/"system")
Content
CreatedAt
ToolCalls (JSON array of tool call objects)
ToolCallId (for tool result messages)
ChannelMessageId, ReplyToChannelMessageId
PromptTokens, CompletionTokens, ElapsedMs
RowId (SQLite rowid, used for compaction boundary)
```

### Connection

```
Id, Name, Type ("telegram"/"whatsapp")
Enabled, AllowNewConversations
ConversationId
Config (JsonElement — type-specific, parsed by factory)
```

### AgentConfig

```
TextProvider, TextModel
VoiceProvider, VoiceModel
CompactionProvider, CompactionModel
MemoryDays (default 3)
MainConversationId
```

Stored at `{dataPath}/config/agent.json`, exposed via `IConfigurable`.

---

## System Prompt Composition

`SystemPromptBuilder` loads markdown files from `{dataPath}` at startup and composes per-conversation:

| File | Text | Voice |
|------|------|-------|
| AGENTS.md | ✓ | ✓ |
| SOUL.md | ✓ | ✓ |
| IDENTITY.md | ✓ | ✓ |
| USER.md | ✓ | ✓ |
| TOOLS.md | ✓ | ✓ |
| MEMORY.md | ✓ | ✓ |
| VOICE.md | | ✓ |

After MEMORY.md: injects recent daily memory files from `{dataPath}/memory/YYYY-MM-DD.md` (last N days, controlled by `AgentConfig.MemoryDays`).

Then: skill catalog XML (`<available_skills>`) if any skills exist.

Then: for each name in `conversation.ActiveSkills`, reads skill body fresh from disk and appends as `<active_skill name="..." directory="...">...</active_skill>`.

---

## SQLite Conversation Store

**Database**: `{dataPath}/conversations.db`

**Schema migration**: `TryAddColumn()` wraps `ALTER TABLE` in try/catch — new columns added without downtime.

**WAL mode** + `PRAGMA foreign_keys=ON`.

**Key operations**:
- `GetOrCreate(id, source, type, provider, model)` — idempotent via `INSERT OR IGNORE`
- `FindOrCreateChannelConversation(channelType, connectionId, chatId)` — queries by binding tuple
- `GetMessages(conversationId)` — prepends `conversation.Context` as system message, then returns live messages after `CompactedUpToRowId`
- Tool calls stored as JSON in `Messages.ToolCalls`; `ActiveSkills` stored as JSON in `Conversations.ActiveSkills`

---

## Channel Providers

### Telegram

- **Polling** (dev): `TelegramBotClient.StartReceiving()`, filters `UpdateType.Message`
- **Webhook** (prod): POST `/api/webhook/telegram/{webhookId}`, HMAC secret validation; webhook stays registered on stop (avoids message loss during restarts)
- **Streaming drafts**: Producer/consumer — background task sends `sendMessageDraft` every 300ms during streaming; disabled for group chats
- **Conversation key**: `(channelType="telegram", connectionId, channelChatId=chatId.ToString())`
- **IOutboundSender**: `SendTextAsync`, `SendHtmlAsync`, `SendTypingAsync`, `SendDraftAsync`
- **Access control**: `AllowedUserIds` list; empty = all blocked

### WhatsApp

- **Architecture**: .NET spawns `node baileys-bridge.js --auth-dir {dir}` as child process; communicates via JSON lines on stdin/stdout
- **Commands sent** (stdin): `{"type":"send","chatId":"...","text":"..."}`, `{"type":"composing","chatId":"..."}`, `{"type":"ping"}`, `{"type":"shutdown"}`
- **Events received** (stdout): `qr`, `connected`, `message`, `disconnected`, `pong`, `error`
- **QR pairing**: `GetQrAsync()` → starts process → waits for `qr` event → user scans → `connected` event
- **Auth state**: persisted by Baileys to `{dataPath}/connections/whatsapp/{connectionId}/`; if files exist on start, bridge starts immediately (skip QR)
- **Reconnect**: exponential backoff 2s–30s, max 10 attempts; resets if previous session lasted >60s
- **Health**: ping every 60s; force reconnect if pong stale >70s
- **Conversation key**: `(channelType="whatsapp", connectionId, channelChatId=JID)`

---

## Tool Capabilities

### FileSystem (file_read, file_write, file_edit, file_append)
- All paths scoped to `{dataPath}` — traversal-protected via `Path.GetFullPath()` validation
- UTF-8 without BOM throughout
- `file_read`: line-numbered output, pagination (`offset`, `limit` default 2000)
- `file_write`: max 1MB
- `file_edit`: exact single-match find-and-replace, returns contextual diff

### Shell (shell_exec)
- `/bin/bash` on Unix; Git Bash or `cmd.exe` on Windows
- Timeout default 30s; on timeout: `process.Kill(entireProcessTree: true)`
- stdout + stderr merged in order
- Output tail-truncated to last 2000 lines / 50KB
- Parameters: `command` (required), `cwd` (optional, relative), `timeout` (optional, seconds)

---

## Skills System

Implements [agentskills.io](https://agentskills.io/specification) open spec — compatible with Claude Code, Cursor, VS Code Copilot, 30+ clients.

**Discovery**: `SkillCatalog` scans `{dataPath}/skills/*/SKILL.md` at startup. Each skill is a markdown file with YAML frontmatter:
```yaml
---
name: my-skill
description: Does something special
---
# Instructions...
```

**Catalog injection**: `<available_skills>` XML appended to every system prompt (~50–100 tokens/skill).

**Activation**: 4 tools exposed to the agent:
1. `activate_skill(name)` — adds to `conversation.ActiveSkills`, persists to SQLite; max 5 active/conversation
2. `deactivate_skill(name)` — removes from list
3. `list_active_skills()` — current active skills with descriptions
4. `activate_skill_resource(skill_name, path)` — loads file from skill directory (max 256KB); result is ephemeral (safe to compact)

**Persistence**: `ActiveSkills` stored as JSON array in SQLite `Conversations` table — survives restarts, survives compaction (in system prompt, not message history).

**Live editing**: Skill body re-read from disk on each prompt build — no restart needed.

---

## Scheduled Tasks

**Trigger types**:
- **Cron** — calendar-based with IANA timezone (e.g., `"0 9 * * 1-5"`, `"America/New_York"`)
- **IntervalMs** — fixed delay in milliseconds
- **At** — one-shot ISO 8601 datetime (typically with `deleteAfterRun: true`)

**Execution** (`ScheduledTaskService`, IHostedService, 30s tick):
1. Find enabled tasks where `nextRunAt <= now`
2. Execute up to 3 concurrently (outside store lock)
3. Prefix prompt: `"[Scheduled task: {name}]\n{prompt}"`
4. Run `ILlmTextProvider.CompleteAsync()` in task's conversation
5. Update `nextRunAt`, `lastRunAt`, `lastStatus`, `consecutiveErrors`

**Delivery** (`DeliveryRouter`):
- If task's conversation is channel-bound (has ChannelType + ConnectionId + ChannelChatId): get running provider from `ConnectionManager`, call `IOutboundSender.SendTextAsync()`
- Unbound conversations: response stays in history only
- Delivery failure ≠ task failure (logged separately)

**LLM-callable tools**: `create_scheduled_task`, `list_scheduled_tasks`, `update_scheduled_task`, `delete_scheduled_task`

---

## Conversation Compaction

Triggered when `LastPromptTokens >= threshold`:

1. Lock conversation (`CompactionRunning = true`)
2. Read live messages, keep latest N message pairs
3. Call `CompactionSummarizer.SummarizeAsync()` — LLM produces `{ context: "...", memories: [...] }`
4. Store summary in `conversation.Context`; set `CompactedUpToRowId`; delete compacted messages from SQLite
5. Append memory entries to daily `{dataPath}/memory/YYYY-MM-DD.md`

On retrieval, `GetMessages()` prepends `Context` as a system message before the live tail.

Tool results are stored as compact summaries (`ToolResultSummary`) in SQLite — full result kept in-memory only for the current turn's tool loop.

---

## Authentication

`AddApiKeyAuth()` registers a custom ASP.NET Core auth scheme:
- Reads `X-Api-Key` header (primary)
- Falls back to `api_key` query parameter (for WebSocket clients)
- Validates against `Authentication:ApiKey` configuration value
- `/health` is anonymous; all other endpoints require authorization

---

## DI Wiring (Startup Order, Program.cs)

1. `AgentEnvironment` — `DataPath` from `DATA_DIR` env var (default `/home/data`)
2. `DataDirectoryBootstrap.Run()` — creates folders, extracts embedded default markdown files (never overwrites)
3. Serilog configured (console + JSONL file, runtime-configurable via `LoggingConfig`)
4. `SystemPromptBuilder`, `AgentLogic` (IAgentLogic)
5. `SqliteConversationStore` (IConversationStore), `FileConfigStore` (IConfigStore), `FileConnectionStore` (IConnectionStore)
6. Tool handlers: `FileSystemToolHandler`, `ShellToolHandler`, `WebFetchToolHandler`, `ExpandToolHandler`, `SkillToolHandler`, `ScheduledTaskToolHandler` — all as `IToolHandler`
7. `SkillCatalog` — scans skills directory
8. LLM providers as keyed singletons + `Func<string, ILlmTextProvider>` factory
9. `VoiceSessionManager` (IVoiceSessionManager)
10. `TelegramChannelProviderFactory`, `WhatsAppChannelProviderFactory` (IChannelProviderFactory)
11. `ConnectionManager` (IConnectionManager + IHostedService) — auto-starts enabled connections
12. `ScheduledTaskService` (IHostedService)
13. `AgentConfig` + all `IConfigurable` services loaded from disk and `Configure()` called
14. Middleware: WebSockets, static files, auth
15. Endpoints registered; SPA fallback to `index.html`

---

## API Reference (all require X-Api-Key)

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/conversations` | List all conversations |
| GET | `/api/conversations/{id}` | Get conversation with messages |
| PATCH | `/api/conversations/{id}` | Update conversation (source, provider, model) |
| DELETE | `/api/conversations/{id}` | Delete conversation |
| POST | `/api/conversations/{id}/messages` | Send message → CompletionEvent JSON array |
| WS | `/ws/conversations/{id}/text` | Stream text CompletionEvents |
| WS | `/ws/conversations/{id}/voice` | Bidirectional voice session |
| WS | `/ws/terminal/{sessionId}` | PTY terminal — binary keystrokes in, binary output out, JSON resize control |
| GET | `/api/connections` | List connections |
| GET | `/api/connections/types` | Channel metadata (config fields, setup steps) |
| POST | `/api/connections` | Create connection |
| PUT | `/api/connections/{id}` | Update connection |
| DELETE | `/api/connections/{id}` | Delete connection |
| POST | `/api/connections/{id}/start` | Start connection |
| POST | `/api/connections/{id}/stop` | Stop connection |
| GET | `/api/scheduled-tasks` | List tasks |
| POST | `/api/scheduled-tasks` | Create task |
| PUT | `/api/scheduled-tasks/{id}` | Update task |
| DELETE | `/api/scheduled-tasks/{id}` | Delete task |
| POST | `/api/scheduled-tasks/{id}/run` | Execute immediately |
| GET | `/api/logs` | List log files |
| GET | `/api/logs/{filename}` | Read log lines (level/time/search/tail filters) |
| GET | `/api/files?path=` | List directory under dataPath |
| GET | `/api/files/content?path=` | Read file as text |
| GET | `/api/files/download?path=` | Download file as attachment |
| POST | `/api/files/rename` | Rename file or directory |
| DELETE | `/api/files?path=` | Delete file or directory |
| POST | `/api/files/mkdir` | Create directory |
| POST | `/api/files/upload` | Upload files (multipart) |
| GET | `/health` | Health check (anonymous) |

---

## Frontend (React SPA)

Desktop-like windowed paradigm:

- **Window manager**: reducer-based state machine (OPEN, CLOSE, FOCUS, MINIMIZE, MAXIMIZE, MOVE, RESIZE), z-index auto-increment on focus, cascade offset 30px per new window
- **App registry**: Text chat, Voice, Conversations, Explorer, Terminal, Scheduled Tasks, Settings — predefined; dynamic windows spawned at runtime (e.g., file viewers)
- **Settings app**: Tabbed sidebar — Agent config, Providers, System Prompt, Connections. Provider forms generated dynamically from backend `ConfigFields` schema (no hardcoded field knowledge in UI)
- **File explorer**: Breadcrumb nav, context menu (view/download/rename/delete), format-specific viewers: `TextViewer` (line numbers), `MarkdownViewer` (frontmatter + rendered), `JsonlViewer` (structured Serilog entries)
- Auth: token from URL hash `#token=...`, added as `X-Api-Key` on all API calls

---

## ChatCLI

Spectre.Console interactive TUI (WebSocket or REST transport):

- `.env` config for server URL + API key
- WebSocket mode: background receive loop, streaming events rendered in real-time
- Tool visualization: `ColorCoded` (default) — color-coded panels by tool domain (file=blue, shell=red, web=orange)
- Navigation: `/back`, `/menu`, `/exit`
