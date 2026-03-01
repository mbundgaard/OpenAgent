# Voice WebSocket Bridge + VoiceSessionManager Relocation

## Context

VoiceSessionManager lives in OpenAgent.Api (HTTP layer) but is not an HTTP concern. The /ws endpoint is an echo stub. We need to wire WebSocket connections to voice sessions and clean up the architecture.

## Design Decisions

1. **Move VoiceSessionManager** from `OpenAgent.Api/Voice/` to `OpenAgent/`. It's session lifecycle infrastructure, not an API concern. Strip conversation state updates — the manager becomes pure session lifecycle (create, track, close, dispose).

2. **Replace `/ws` echo** with `GET /ws/conversations/{id}/voice`. Conversation ID comes from the URL path.

3. **Binary for audio, JSON for events.** Audio frames are binary WebSocket messages (raw PCM16). Transcripts, speech signals, and errors are JSON text messages.

4. **Bridge pattern.** The WebSocket handler runs two concurrent tasks: a read loop (WS → voice session) and a write loop (voice session → WS). Both terminate on disconnect.

## WebSocket Protocol

### Inbound (client to server)

| WS Message Type | Meaning |
|-----------------|---------|
| Binary | Raw PCM16 audio → `session.SendAudioAsync()` |
| Text (future) | JSON control commands (commit, cancel) |

### Outbound (server to client)

| WS Message Type | Meaning |
|-----------------|---------|
| Binary | Raw PCM16 audio from `AudioDelta` events |
| Text | JSON event (see below) |

### Outbound JSON Events

```json
{ "type": "speech_started" }
{ "type": "speech_stopped" }
{ "type": "audio_done" }
{ "type": "transcript_delta", "text": "...", "source": "user" }
{ "type": "transcript_done", "text": "...", "source": "assistant" }
{ "type": "error", "message": "..." }
```

## Handler Flow

```
GET /ws/conversations/{id}/voice
  1. Accept WebSocket upgrade
  2. Validate conversation exists (close with error if not)
  3. Get or create voice session via VoiceSessionManager
  4. Run concurrently:
     a. Read loop: read WS Binary frames → session.SendAudioAsync()
     b. Write loop: session.ReceiveEventsAsync() → write WS Binary/Text frames
  5. On disconnect or error: close session, close WebSocket
```

## Project Changes

| Action | File |
|--------|------|
| Move | `OpenAgent.Api/Voice/VoiceSessionManager.cs` → `OpenAgent/VoiceSessionManager.cs` |
| Delete | `OpenAgent.Api/Voice/` directory |
| Modify | `OpenAgent/VoiceSessionManager.cs` — change namespace, strip conversation state updates |
| Rewrite | `OpenAgent.Api/WebSockets/WebSocketEndpoints.cs` — replace echo with voice bridge |
| Modify | `OpenAgent/Program.cs` — remove `using OpenAgent.Api.Voice`, update endpoint mapping |
