# Slash Commands — Design Document

## Problem

Users interact with Comput through channels (Telegram, WhatsApp, web chat). Some operations don't need the LLM — clearing a conversation, switching models, checking status. Today these require the settings UI or direct API calls. Slash commands give users direct control from the chat interface.

## How It Works

Slash commands are intercepted **before** the message reaches the LLM. The handler checks if the message starts with `/`, parses the command and arguments, executes it, and returns a direct response. The message never enters the conversation history or triggers an LLM completion.

### Interception Points

All four message entry points need the same check:

| Entry Point | File | Location |
|---|---|---|
| Telegram | `TelegramMessageHandler.cs` | After message filtering, before `CompleteAsync` |
| WhatsApp | `WhatsAppMessageHandler.cs` | After deduplication, before `CompleteAsync` |
| WebSocket | `WebSocketTextEndpoints.cs` | After message parse, before `CompleteAsync` |
| REST | `ChatEndpoints.cs` | After validation, before `CompleteAsync` |

A central `SlashCommandHandler` keeps command logic in one place. Each entry point calls it with the raw text, conversation ID, and a response callback.

### Response Handling

Slash command responses go directly to the user without entering the conversation. On Telegram that's a `sendMessage`. On WebSocket it's a text frame. The response is **not** added to conversation history — it's ephemeral.

## Proposed Commands

### Conversation Management

| Command | Description |
|---|---|
| `/clear` | Delete all messages in current conversation, reset context. Conversation itself stays. |
| `/new` | Start a fresh conversation (new ID) in the same chat. Old conversation is preserved. |
| `/info` | Show current conversation: ID, provider, model, turn count, last prompt tokens, active skills. |
| `/compact` | Trigger compaction manually — useful when context is getting large. |

### Provider & Model

| Command | Description |
|---|---|
| `/model <provider> <model>` | Switch provider and model for this conversation. E.g. `/model anthropic-subscription claude-sonnet-4-5-20250514`. |
| `/model` | Show current provider/model. |

### Skills

| Command | Description |
|---|---|
| `/skills` | List available skills with descriptions. |
| `/skill <name>` | Activate a skill. |
| `/unskill <name>` | Deactivate a skill. |

### Memory

| Command | Description |
|---|---|
| `/memory` | Show today's memory file. |
| `/remember <text>` | Append a line to today's memory file. Quick capture without asking the LLM. |

### System

| Command | Description |
|---|---|
| `/reload` | Reload system prompt files and skills from disk. Same as the settings UI button. |
| `/status` | Show system status: provider config, active connections, skill count, uptime. |
| `/tokens` | Show last prompt token count for this conversation — quick check on context size. |

### Admin (Potentially Restricted)

| Command | Description |
|---|---|
| `/unlock` | Set `allowNewConversations = true` on the connection. |

## What Slash Commands Are NOT

- **Not LLM tools.** The LLM never sees them. They're imperative, synchronous operations.
- **Not stored in history.** Neither the command nor the response enters the conversation.
- **Not a replacement for the settings UI.** Complex config (provider setup, connection creation) stays in the UI. Slash commands handle quick, frequent operations.

## Architecture

```
User message: "/clear"
    │
    ▼
Channel Handler (Telegram/WhatsApp/WS/REST)
    │
    ├─ Starts with "/"? ──► SlashCommandHandler.TryHandle(text, conversationId)
    │                           │
    │                           ├─ Parse command + args
    │                           ├─ Execute (IConversationStore, AgentConfig, SkillCatalog, etc.)
    │                           └─ Return response text
    │                           
    │   ◄── Response sent directly to channel (not added to conversation)
    │
    └─ Normal message ──► LLM completion flow (unchanged)
```

### SlashCommandHandler

Single class, injected with the services it needs:
- `IConversationStore` — for /clear, /info, /compact
- `AgentConfig` — for /model
- `SkillCatalog` — for /skills
- `SystemPromptBuilder` — for /reload

Returns `(bool handled, string? response)`. If `handled` is false, the message flows to the LLM normally. This means unknown `/` commands still reach the LLM (e.g. the user might say "/data directory looks empty").

### Conflict with Telegram Bot Commands

Telegram has its own `/command` system (BotFather menu commands). These are complementary — BotFather commands provide autocomplete hints in the Telegram UI, but the actual handling is the same `SlashCommandHandler`. Register the key commands with BotFather for discoverability:

```
clear - Reset conversation
model - Show or switch model
skills - List available skills
info - Conversation info
status - System status
```

## Benefits

1. **Speed** — `/clear` is instant, no LLM round-trip
2. **Control** — switch models mid-conversation without leaving the chat
3. **Transparency** — `/info` and `/tokens` let users understand system state
4. **Channel-native** — works everywhere (Telegram, WhatsApp, web) with no UI required
5. **Dog-fooding** — essential for managing Comput in production from Telegram

## Open Questions

- Should `/clear` ask for confirmation or just do it?
- Should `/new` preserve the old conversation or delete it?
- Do we need `/help` listing available commands?
- Should some commands be restricted to specific users (admin vs regular)?
- How do we handle `/` messages that aren't commands (e.g. "I have 2/3 of the data")?
  - Current approach: only exact command matches at start of message. "2/3" doesn't start with `/`.
  - Edge case: "/data is missing" — not a known command, falls through to LLM.
