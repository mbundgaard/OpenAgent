# Chat CLI UI Redesign

## Goal

Replace the plain-text CLI with a polished interactive experience using Spectre.Console. Arrow-key menus, live streaming output, colored panels, and clear navigation.

## Flow

```
Server Select → Mode Select (Voice / Text) → [Text: Transport (REST / WebSocket)] → Conversation Select → Chat Loop
```

## Navigation

- `/back` — one level up (chat → conversations → mode → server)
- `/menu` — jump to server select
- `/exit` or `quit` — close app

## Dependency

- `Spectre.Console` NuGet package

## Screens

### Launch

FigletText header "OpenAgent", then straight to server select.

### Server Select

Spectre `SelectionPrompt` with arrow keys:
- localhost (http://localhost:5264)
- openagent-test (https://openagent-test.azurewebsites.net)

### Mode Select

SelectionPrompt:
- Text
- Voice

### Transport Select (text mode only)

SelectionPrompt:
- WebSocket (streaming)
- REST

### Conversation Select

SelectionPrompt:
- New conversation
- Existing conversations listed with truncated ID, type, and date (fetched from server)

### Chat Loop

- Rule divider showing connected server + mode at top
- User input with `>` markup prompt
- Spinner ("Thinking...") while waiting for first token
- Live inline streaming as tokens arrive
- Rule divider between messages
- `/back` returns to conversation select
- `/menu` returns to server select

## Architecture

Single `Program.cs` file — the CLI is intentionally flat. Extract methods for each screen:
- `SelectServer()` — returns base URL
- `SelectMode()` — returns "text" or "voice"
- `SelectTransport()` — returns "rest" or "websocket"
- `SelectConversation(http)` — returns conversation ID
- `RunRestLoop(http, conversationId)` — REST chat loop
- `RunWebSocketLoop(baseUrl, conversationId)` — WebSocket streaming chat loop

Each method returns a navigation signal (continue, back, exit) to drive the flow.
