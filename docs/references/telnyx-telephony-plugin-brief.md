# OpenAgent – Telnyx Telephony Plugin

## Overview

Build a telephony plugin for OpenAgent that enables voice calls via Telnyx. The plugin connects phone calls to OpenAgent's existing voice session pipeline using Telnyx's WebSocket media streaming. Each agent gets a dedicated phone number (DID) and supports both inbound and outbound calls.

## Repository

- **OpenAgent**: https://github.com/mbundgaard/OpenAgent
- **Stack**: TypeScript / Node.js
- **Existing voice**: WebSocket-based voice sessions using GPT Realtime-compatible providers

## Telnyx Resources

- **Media Streaming over WebSockets**: https://developers.telnyx.com/docs/voice/programmable-voice/media-streaming
- **Call Control API**: https://developers.telnyx.com/api/call-control
- **Streaming Start API**: https://developers.telnyx.com/api/call-control/start-call-streaming
- **TeXML Stream verb**: https://developers.telnyx.com/docs/voice/programmable-voice/texml-verbs/stream
- **IVR demo (Python, but good reference)**: https://developers.telnyx.com/docs/voice/programmable-voice/ivr-demo
- **Sample apps (bidirectional WebSocket echo)**: https://github.com/team-telnyx/telnyx-samples-pwc
- **Voice API pricing**: https://telnyx.com/pricing/voice-api
- **Number pricing**: https://telnyx.com/pricing/numbers

## Call Modes

The plugin supports two modes, switchable mid-call via DTMF:

### Conversation Mode (default)
- Bidirectional audio streaming
- Audio is sent to OpenAgent's voice pipeline (GPT Realtime)
- Agent listens and responds
- This is the default when a call connects

### Listen Mode
- Audio is streamed inbound only (transcribe/log)
- Agent does NOT send audio back to the call
- Used when the agent is merged into a live customer call as a silent observer

### DTMF Mode Switching
- `*` (star) → **Listen mode** (mute agent — "closed mouth")
- `#` (hash) → **Conversation mode** (unmute agent — "open mouth")
- Default on call connect: **Conversation mode**
- DTMF detection must remain active throughout the call

## Call Flows

### Inbound Call (someone calls the agent)
1. Caller dials agent's Telnyx DID number
2. Telnyx sends webhook to OpenAgent server (`call.initiated` event)
3. Server answers the call via Call Control API
4. Server starts bidirectional WebSocket media stream (`streaming_start`)
5. Audio flows to/from OpenAgent voice pipeline in conversation mode
6. Caller can press `*` / `#` to toggle modes mid-call

### Outbound Call (agent calls someone)
1. OpenAgent decides to place a call (triggered by agent logic or user action)
2. Server sends `POST /v2/calls` with:
   - `to`: destination number
   - `from`: agent's DID number
   - `stream_url`: WebSocket endpoint for audio
   - `stream_track`: `both_tracks`
3. Recipient's phone rings showing agent's DID as caller ID
4. On answer, bidirectional audio streams to voice pipeline
5. Same DTMF mode switching applies

### Conference / Merge Scenario
1. User is on a regular phone call with a customer
2. User taps "add call" on their phone, dials agent's number
3. Agent answers in conversation mode (says "hi")
4. User presses `*` → agent switches to listen mode (silent transcription)
5. User merges the calls — agent silently transcribes the conversation
6. User presses `#` → agent switches back to conversation mode, participates with full transcript context

## Audio Format

Telnyx supports these codecs for WebSocket streaming:
- PCMU (8 kHz) — default
- PCMA (8 kHz)
- G722 (8 kHz)
- OPUS (8 kHz, 16 kHz)
- AMR-WB (8 kHz, 16 kHz)
- **L16 (raw linear PCM, 16 kHz)** — preferred, as GPT Realtime natively uses PCM16

Use `L16` codec to avoid transcoding overhead. Set via `stream_bidirectional_codec: "L16"` on stream start.

## WebSocket Message Format (Telnyx → Server)

Messages arrive as JSON over the WebSocket:

```json
{ "event": "connected" }

{ "event": "start", "stream_id": "...", "call_control_id": "...", "metadata": {...} }

{
  "event": "media",
  "stream_id": "...",
  "media": {
    "track": "inbound_track",
    "chunk": "1",
    "timestamp": "...",
    "payload": "<base64-encoded audio>"
  }
}

{ "event": "stop", "stream_id": "..." }
```

## WebSocket Message Format (Server → Telnyx)

Send audio back as RTP via WebSocket (bidirectional mode):

```json
{
  "event": "media",
  "stream_id": "...",
  "media": {
    "payload": "<base64-encoded audio>"
  }
}
```

Clear queued audio:
```json
{ "event": "clear", "stream_id": "..." }
```

## Agent Routing

Each agent gets its own DID number. Inbound calls are routed based on the `to` number in the webhook payload. The server maps DIDs to agent configurations.

Example config structure:
```json
{
  "agents": {
    "+4512345678": {
      "name": "Sales Agent",
      "voice_provider": "llm_voice_openai",
      "soul": "data/workspace/SOUL_SALES.md"
    },
    "+4587654321": {
      "name": "Support Agent",
      "voice_provider": "llm_voice_openai",
      "soul": "data/workspace/SOUL_SUPPORT.md"
    }
  }
}
```

## Plugin Structure

Follow OpenAgent's existing plugin pattern in `src/plugins/`. The plugin should contain:

- **Webhook handler**: HTTP endpoint for Telnyx call control webhooks
- **WebSocket server**: Endpoint that Telnyx connects to for media streaming
- **Call manager**: Tracks active calls, maps call_control_ids to agent sessions and current mode
- **DTMF handler**: Processes `*` and `#` to toggle conversation/listen mode
- **Outbound call API**: Method for agent logic to initiate calls
- **Bridge to voice pipeline**: Connects Telnyx audio stream to OpenAgent's existing voice session infrastructure

## Environment / Config

Required configuration:
- `TELNYX_API_KEY`: API key from Mission Control portal
- `TELNYX_APP_ID`: Call Control Application ID (connection_id)
- `TELNYX_WEBHOOK_URL`: Public URL for receiving call webhooks
- `TELNYX_STREAM_URL`: Public WSS URL for media streaming

## Tasks

1. **Research & spike**: Review Telnyx Call Control and media streaming docs, understand webhook flow and WebSocket protocol
2. **Plugin scaffold**: Create plugin structure following OpenAgent conventions
3. **Webhook handler**: Implement HTTP endpoint for `call.initiated`, `call.answered`, `call.hangup`, `dtmf.received` events
4. **WebSocket media server**: Implement WSS endpoint that receives/sends audio in Telnyx format
5. **Voice pipeline bridge**: Connect Telnyx audio stream to OpenAgent's existing voice session (GPT Realtime)
6. **DTMF mode switching**: Implement `*` for listen mode, `#` for conversation mode, with state per call
7. **Outbound calls**: Implement API for agent to initiate calls with caller ID and stream setup
8. **Agent routing**: Map inbound DID numbers to agent configurations
9. **Call state management**: Track active calls, modes, transcripts, cleanup on hangup
10. **Integration testing**: Test inbound, outbound, mode switching, and conference/merge scenarios

## Notes

- Telnyx pricing: ~$1/month per DID + ~$0.0075/min inbound, ~$0.009/min outbound. No subscription or platform fee.
- Danish +45 numbers available via Telnyx Mission Control portal.
- DTMF detection must remain active for the full duration of the call (not just during IVR gather).
- When in listen mode during a merged call, the agent should still be transcribing and building context so it can participate intelligently when switched back to conversation mode.
- L16 codec at 16 kHz is the preferred choice to minimize latency with GPT Realtime.
