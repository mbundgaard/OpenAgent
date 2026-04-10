# Code Review Prompts

Generated from a directory scan and coupling analysis of the OpenAgent codebase.
Run these prompts sequentially or in parallel to conduct a full review.

---

## Domain Overview

| # | Domain | Primary Risk |
|---|--------|-------------|
| 1 | Security & Trust Boundary | Path traversal, SSRF, command injection, flat privilege model |
| 2 | LLM Provider Implementations | Streaming teardown, tool call loop, error propagation |
| 3 | Core Agent Architecture | Architectural invariants, DI correctness, lazy resolution |
| 4 | Channel Providers | State machine correctness, Baileys bridge framing, delivery guarantees |
| 5 | Conversation Storage & Compaction | Data integrity, schema migration, orphaned tool calls |
| 6 | Skills System | Spec compliance, XML injection in catalog, resource path safety |
| 7 | Terminal & Scheduled Tasks | Native interop leaks, cron edge cases, untestable concrete deps |
| 8 | API Endpoints | Endpoint thinness, privilege separation, input validation |
| 9 | Frontend | Token storage, WebSocket cleanup, memory leaks |
| 10 | Coupling & Contracts Completeness | Missing interfaces, inconsistent DI registration pattern |
| 11 | Test Coverage Gaps | Untested high-risk surfaces |

---

## Prompt 1 — Security & Trust Boundary

Read the following files in full and review for security vulnerabilities:
- `OpenAgent.Tools.Shell/ShellExecTool.cs` and `ShellToolHandler.cs`
- `OpenAgent.Tools.FileSystem/` — all 5 files
- `OpenAgent.Tools.WebFetch/UrlValidator.cs`, `IDnsResolver.cs`, `WebFetchTool.cs`
- `OpenAgent.Security.ApiKey/` — all 3 files
- `OpenAgent.Api/Endpoints/WebSocketTerminalEndpoints.cs`
- `OpenAgent.Api/Endpoints/AdminEndpoints.cs`

For each file, check:

1. **Shell tool**: command injection via argument construction, process tree kill completeness, timeout enforcement, merged stdout/stderr — any way to escape the timeout or leave orphan processes?
2. **FileSystem tools**: path traversal — is the `dataPath` prefix check resistant to `../../`, symlinks, null bytes, encoded separators?
3. **WebFetch**: SSRF/DNS rebinding — does `UrlValidator` + `IDnsResolver` block private IP ranges, loopback, link-local? Is there a TOCTOU gap between validation and fetch?
4. **API key auth**: constant-time comparison? What happens on missing header — 401 or 500? Is `/health` the only anonymous route?
5. **Terminal endpoint**: static `ActiveBridges` dictionary — race condition between `TryRemove` in `finally` and a concurrent new connection inserting itself. PTY has unsandboxed shell access — is there any restriction on what `sessionId` can be?
6. **AdminEndpoints**: flat privilege model (same API key as all other endpoints) — should credential-writing operations require a separate elevated key? No audit logging of config changes — is that acceptable?

Flag: any vulnerability, the severity, and a concrete fix.

---

## Prompt 2 — LLM Provider Implementations

Read the following files in full:
- `OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs` and its `Models/`
- `OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs` and its `Models/`
- `OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiRealtimeVoiceProvider.cs` and `AzureOpenAiVoiceSession.cs`

Review for:

1. **Tool call loop**: is the 10-round safety cap enforced in both providers consistently? What happens on round 11 — hard error, graceful stop, or silent truncation?
2. **Streaming teardown**: when the caller cancels the `IAsyncEnumerable`, does the HTTP connection get disposed cleanly? Any risk of background tasks lingering?
3. **Anthropic-specific**: is `Authorization: Bearer` set per-request (not on `DefaultRequestHeaders`)? Are `anthropic-beta`, `x-app`, `user-agent` identity headers present? Is the system prompt sent as a text block array? Is adaptive thinking applied only for 4.6 models?
4. **Error propagation**: do API errors (4xx, 5xx, malformed JSON) surface as meaningful exceptions to the caller, or get swallowed?
5. **Orphaned tool calls**: what happens if the LLM returns a tool call but execution throws — is the tool result still added to the message history to avoid API 400 errors on the next round?

---

## Prompt 3 — Core Agent Architecture

Read the following files in full:
- `OpenAgent/AgentLogic.cs`
- `OpenAgent/SystemPromptBuilder.cs`
- `OpenAgent/DataDirectoryBootstrap.cs`
- `OpenAgent/AgentConfigConfigurable.cs`
- `OpenAgent/Program.cs`

Review for:

1. **IAgentLogic contract**: does `AgentLogic` stay as injected context only? Verify it does not call providers, orchestrate completions, or drive the conversation loop — that must be the provider's job.
2. **System prompt composition**: is per-`ConversationType` filtering correct? Are active skills injected per-conversation (not globally)? Could two concurrent conversations cross-contaminate each other's prompt?
3. **Lazy provider resolution**: is `Func<string, ILlmTextProvider>` truly resolved per-message? Could a stale provider reference be captured in a closure?
4. **Bootstrap**: does `DataDirectoryBootstrap` guarantee never-overwrite on existing personality files? Any TOCTOU between exists-check and write?
5. **DI wiring in Program.cs**: are all tool handlers registered via the same pattern, or is it inconsistent? (`AddScheduledTasks()` extension exists — do FileSystem, Shell, WebFetch, Expand, Skills follow suit?)
6. **TODO at AgentLogic.cs:21**: assess impact of the unimplemented channel-specific prompt variant feature — is the current behaviour correct as a fallback?

---

## Prompt 4 — Channel Providers

Read the following files in full:
- `OpenAgent.Channel.Telegram/` — all 8 files
- `OpenAgent.Channel.WhatsApp/` — all 8 .cs files (exclude `node/`)

Review for:

1. **WhatsApp state machine**: trace the lifecycle `unpaired → pairing (QR) → connected → LoggedOut`. Is every transition handled? What happens if the Node process crashes mid-pairing?
2. **Baileys bridge framing**: stdin/stdout JSON line protocol — is there a maximum line length? What happens on a partial write or a line with embedded newlines?
3. **Reconnect backoff**: is the 2s→30s, max-10 backoff correctly implemented? Is there a cap to prevent infinite reconnect loops after `LoggedOut`?
4. **Telegram idempotency**: webhook re-registration on `StartAsync` — does it handle the case where the previous webhook URL differs from the new one?
5. **Streaming drafts**: how are in-progress message edits handled if the connection drops mid-stream?
6. **IOutboundSender**: delivery guarantees — fire-and-forget or confirmed? What happens if the channel is disconnected when a scheduled task tries to deliver?
7. **Per-chat conversation ID**: `{channelType}:{connectionId}:{chatId}` — any collision risk if `chatId` contains a colon?

---

## Prompt 5 — Conversation Storage & Compaction

Read the following files in full:
- `OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`
- `OpenAgent.Compaction/CompactionSummarizer.cs`
- `OpenAgent.Compaction/CompactionPrompt.cs`
- `OpenAgent.Models/Conversations/Conversation.cs`
- `OpenAgent.Models/Conversations/Message.cs`

Review for:

1. **Schema migration**: `TryAddColumn` — does it cover all columns currently in use? What happens if a column is renamed rather than added?
2. **ActiveSkills JSON column**: how is it serialized/deserialized? What happens on a null or malformed value?
3. **Compaction correctness**: what exactly gets stripped vs. preserved? Are active skill bodies re-injected after compaction? Are tool call/result pairs kept together or can they be split?
4. **Orphaned tool call handling**: does `BuildChatMessages` correctly skip lone tool calls (no matching result) to prevent API 400 errors?
5. **Thread safety**: is `SqliteConversationStore` safe for concurrent access across multiple channel providers hitting the same conversation?
6. **`GetMessagesByIds`** (used by ExpandTool): does it validate that the requested IDs belong to the given conversation, or can a caller retrieve messages from any conversation?

---

## Prompt 6 — Skills System

Read the following files in full:
- `OpenAgent.Skills/SkillDiscovery.cs`
- `OpenAgent.Skills/SkillCatalog.cs`
- `OpenAgent.Skills/SkillToolHandler.cs`
- `OpenAgent.Skills/SkillFrontmatterParser.cs`
- `OpenAgent.Skills/SkillEntry.cs`

Review for:

1. **YAML frontmatter parsing**: is it robust against missing fields, extra whitespace, multi-line values, or malformed YAML? What happens on a SKILL.md with no frontmatter?
2. **Catalog injection**: how is the `<available_skills>` XML block built? Any risk of skill name or description containing XML that breaks the block structure?
3. **`activate_skill_resource` path safety**: does the resource path resolution prevent traversal outside the skill's own directory (e.g., `../../config/agent.json`)?
4. **`ExecuteAsync` and conversation state**: skill activation modifies `Conversation.ActiveSkills` — is this done atomically with the store write? Any race if two messages arrive simultaneously?
5. **Catalog token budget**: is there a guard against a skills directory growing so large that the catalog overflows the context window?
6. **Coupling**: `SkillCatalog` is used as a concrete type in `SystemPromptBuilder` (cross-project). Should `ISkillCatalog` be in `OpenAgent.Contracts`?

---

## Prompt 7 — Terminal & Scheduled Tasks

Read the following files in full:
- `OpenAgent.Terminal/Native/PtyInterop.cs`
- `OpenAgent.Terminal/PtyTerminalSession.cs`
- `OpenAgent.Terminal/ProcessTerminalSession.cs`
- `OpenAgent.Terminal/TerminalSessionManager.cs`
- `OpenAgent.ScheduledTasks/ScheduledTaskService.cs`
- `OpenAgent.ScheduledTasks/ScheduledTaskExecutor.cs`
- `OpenAgent.ScheduledTasks/ScheduleCalculator.cs`
- `OpenAgent.ScheduledTasks/DeliveryRouter.cs`
- `OpenAgent.ScheduledTasks/Storage/ScheduledTaskStore.cs`

Review for:

1. **PTY native interop**: are all `GCHandle`/`SafeHandle` usages correct? Any unmanaged resource leaks on session close or process crash?
2. **Session cleanup**: when a WebSocket disconnects, does the PTY process get killed? Or does it linger and accumulate?
3. **ProcessTerminalSession vs PtyTerminalSession**: when is each used and why? Is the fallback path correct?
4. **Cron calculation**: does `ScheduleCalculator` handle DST transitions, leap seconds, and timezone edge cases correctly?
5. **Delivery**: `DeliveryRouter` has no interface — is it tested? What happens if the target conversation's channel is offline at delivery time?
6. **Webhook trigger**: what does the webhook context body get injected into — the conversation, the system prompt, or a tool call?
7. **Coupling**: `DeliveryRouter` and `ScheduledTaskService` are concrete with no interfaces in `OpenAgent.Contracts` — impacts testability.

---

## Prompt 8 — API Endpoints

Read the following files in full from `OpenAgent.Api/Endpoints/`:
- `ChatEndpoints.cs`, `ConversationEndpoints.cs`, `ConnectionEndpoints.cs`
- `ScheduledTaskEndpoints.cs`, `LogEndpoints.cs`, `FileExplorerEndpoints.cs`
- `WebSocketVoiceEndpoints.cs`, `WebSocketTextEndpoints.cs`
- `AdminEndpoints.cs`, `WebSocketTerminalEndpoints.cs`

Review for:

1. **Endpoint thinness**: is any business logic present in endpoints that should live in a service or provider?
2. **AdminEndpoints privilege**: config writes use the same API key as read operations — is there a case for separating admin operations behind a different key or role?
3. **AdminEndpoints doc comment bug**: XML doc comments at lines 34–37 are inside a method body — they are dead code, not actual documentation.
4. **Input validation**: do endpoints validate route/query parameters before forwarding? Any missing null checks or unbounded inputs (e.g., log `limit` with no cap)?
5. **WebSocket session lifecycle**: do voice and text WebSocket endpoints clean up properly on disconnect? Are there equivalent `ActiveBridges`-style race conditions as found in the terminal endpoint?
6. **FileExplorer**: does it enforce `dataPath` scoping the same way the FileSystem tools do, or is it a separate (possibly weaker) implementation?

---

## Prompt 9 — Frontend

Read the following files in full from `src/web/src/`:
- `auth/token.ts`, `auth/api.ts`
- `hooks/useWindowManager.ts`
- `apps/chat/hooks/useTextStream.ts`, `useVoiceSession.ts`, `useConversation.ts`
- `apps/settings/api.ts`, `SettingsApp.tsx`, `ConnectionsForm.tsx`
- `apps/terminal/TerminalApp.tsx`
- `windows/WindowFrame.tsx`

Review for:

1. **Token storage**: where is the API key stored — `localStorage`, `sessionStorage`, memory? What is the XSS exposure?
2. **WebSocket hooks**: do `useTextStream` and `useVoiceSession` clean up (`ws.close()`) on component unmount? Any risk of stale closures sending to a closed socket?
3. **Window manager**: does `useWindowManager` remove event listeners and clear references on unmount? Any memory leak from accumulated window state?
4. **Settings dynamic forms**: are config values sanitized before display? Could a malicious provider config field name cause XSS via `dangerouslySetInnerHTML` or similar?
5. **Terminal app**: does `TerminalApp` handle WebSocket reconnect after eviction (when a second tab opens the same session)? Is the xterm.js instance disposed on unmount?
6. **Error boundaries**: are async errors from API calls surfaced to the user or silently swallowed?

---

## Prompt 10 — Coupling & Contracts Completeness

Read the following files:
- All files in `OpenAgent.Contracts/` (18 interfaces)
- `OpenAgent/SystemPromptBuilder.cs`
- `OpenAgent.Skills/SkillToolHandler.cs`
- `OpenAgent.ScheduledTasks/ScheduledTaskService.cs`
- `OpenAgent.ScheduledTasks/DeliveryRouter.cs`
- `OpenAgent.ScheduledTasks/ServiceCollectionExtensions.cs`
- `OpenAgent/Program.cs`

Review for:

1. **Missing interfaces**: `SkillCatalog`, `DeliveryRouter`, `ScheduledTaskService` all cross project boundaries without an interface in Contracts. List every concrete type that leaks across a project boundary.
2. **Extension method consistency**: `AddScheduledTasks()` exists but FileSystem, Shell, WebFetch, Expand, and Skills tools are registered inline in Program.cs. Flag the inconsistency.
3. **`AgentConfig` and `AgentEnvironment`**: they live in Contracts as concrete classes, not interfaces. Is this intentional (they are value objects)? Or should they be interfaces for testability?
4. **`ExpandTool` convention violation**: uses an anonymous type in `JsonSerializer.Serialize` — project convention requires named models with `[JsonPropertyName]` attributes.
5. For each missing interface found, suggest: the interface name, which methods it should expose, and which project it belongs in.

---

## Prompt 11 — Test Coverage Gaps

Review the test project at `OpenAgent.Tests/` and the full list of production files.

The following production areas have zero test coverage. For each, assess the risk of no coverage and suggest the most valuable test cases:

1. **`AdminEndpoints`** — credential write, secret masking on GET, partial merge logic in POST
2. **`WebSocketTerminalEndpoints`** — bridge eviction race condition, session cleanup on disconnect
3. **`CompactionSummarizer`** — what gets stripped, active skills preservation across compaction
4. **`DeliveryRouter`** — offline channel behaviour, WebSocket delivery path, outbound sender routing
5. **`ShellExecTool`** — timeout enforcement, process tree kill, stdout/stderr merge
6. **`FileSystemToolHandler`** — path traversal attempts, dataPath boundary enforcement
7. **`SystemPromptBuilder`** — per-ConversationType output correctness, active skills injection
8. **`AgentLogic`** — tool routing by name, message history management, round-trip with fakes
9. **`PtyTerminalSession` / `PtyInterop`** — session cleanup on disconnect, resize handling

For each area, also flag whether a unit test or integration test (via `WebApplicationFactory`) is more appropriate.
