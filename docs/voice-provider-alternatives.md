# Voice Provider Alternatives — Getting Started Primer

This document covers two alternatives to the Azure OpenAI Realtime voice provider currently used in OpenAgent:
**xAI Grok Voice Agent API** and **Google Gemini Live API**.

Both implement the same conceptual contract as `ILlmVoiceProvider` / `IVoiceSession`, but differ significantly in protocol,
effort to implement, and trade-offs.

-----

## At a Glance

|                     |**Grok Voice Agent**                           |**Gemini Live**                                       |
|---------------------|-----------------------------------------------|------------------------------------------------------|
|Protocol             |OpenAI Realtime-compatible WebSocket           |Proprietary WebSocket                                 |
|Endpoint             |`wss://api.x.ai/v1/realtime`                   |`wss://generativelanguage.googleapis.com/ws/...`      |
|Auth                 |`Authorization: Bearer {API_KEY}` header       |`?key={API_KEY}` query string (or OAuth for Vertex AI)|
|Audio in             |PCM16 (8–48 kHz), G.711 μ-law/A-law, base64    |PCM16 (16 kHz recommended), base64                    |
|Audio out            |PCM16 / G.711, base64                          |PCM16 (24 kHz), base64                                |
|VAD                  |Server-side (built-in)                         |Server-side (built-in)                                |
|Tool calling         |Yes — same schema as OpenAI Realtime           |Yes — proprietary `toolCall` / `toolResponse` format  |
|Session model        |Stateful per WebSocket connection              |Stateful per WebSocket connection                     |
|Pricing (approx.)    |~$0.05/min                                     |Token-based; audio input ~$1.00/1M tokens             |
|Implementation effort|**Low** — reuse most of existing Azure provider|**Medium** — new protocol mapping required            |

-----

## 1. Grok Voice Agent API

### Why it is the easiest migration

Grok follows the **OpenAI Realtime API specification** — the same WebSocket event schema OpenAgent already speaks
in `AzureOpenAiRealtimeVoiceProvider`. The implementation is largely a configuration swap:
different WSS endpoint, different auth header, and potentially a model name change.

### API Key

Get an API key from [console.x.ai](https://console.x.ai). Set it in your OpenAgent config store under a new
`GrokRealtimeVoiceProvider` key (following the existing `IConfigurable` pattern).

### Connecting

```
WSS endpoint:  wss://api.x.ai/v1/realtime
Auth header:   Authorization: Bearer {XAI_API_KEY}
```

No extra headers required. The Azure provider uses `api-key` and an Azure-specific URL — that is all that changes
at the connection level.

### Session Configuration

Send a `session.update` event immediately after connecting (same as Azure OpenAI):

```json
{
  "type": "session.update",
  "session": {
    "model": "grok-4-1-fast-non-reasoning",
    "voice": "eve",
    "instructions": "<your system prompt here>",
    "turn_detection": { "type": "server_vad" },
    "input_audio_format": "pcm16",
    "output_audio_format": "pcm16",
    "input_audio_transcription": { "model": "whisper-1" },
    "tools": []
  }
}
```

> **Note on model name:** Use the non-reasoning variant for realtime. `grok-4-1-fast-non-reasoning` is the current
> recommended model for voice. Reasoning models add latency that is unacceptable for interactive voice.

### Voice Options

|Voice |Character                                              |
|------|-------------------------------------------------------|
|`eve` |Energetic female — default                             |
|`ara` |Warm female                                            |
|`rex` |Professional and articulate, male confident (business) |
|`sal` |Neutral                                                |
|`leo` |Authoritative male                                     |

Authoritative list: `GET /v1/tts/voices`.

### Audio Streaming

Identical to the Azure provider. Send PCM16 audio frames as base64 via `input_audio_buffer.append`:

```json
{
  "type": "input_audio_buffer.append",
  "audio": "<base64-encoded PCM16>"
}
```

Commit the buffer when the turn ends (if not using server VAD to auto-detect):

```json
{ "type": "input_audio_buffer.commit" }
```

### Event Stream (received events)

The event types you already handle map directly:

|OpenAI/Azure event                     |Grok equivalent                         |Notes                         |
|---------------------------------------|----------------------------------------|------------------------------|
|`response.audio.delta`                 |`response.output_audio.delta`           |Audio payload in `delta` field|
|`response.audio_transcript.delta`      |`response.output_audio_transcript.delta`|Transcript chunk              |
|`input_audio_buffer.speech_started`    |`input_audio_buffer.speech_started`     |Identical                     |
|`response.function_call_arguments.done`|`response.function_call_arguments.done` |Identical                     |
|`error`                                |`error`                                 |Identical                     |
|`session.created`                      |`session.created`                       |Identical                     |


> **Minor event name difference:** Grok uses `response.output_audio.delta` where Azure uses `response.audio.delta`.
> This is the most likely deviation to catch. Check your `ReceiveEventsAsync()` switch/match statement.

### Tool Calling

Follows the OpenAI Realtime tool call flow exactly:

1. `response.function_call_arguments.done` event received
1. Execute tool locally via `IAgentLogic.ExecuteToolAsync()`
1. Send result back:

```json
{
  "type": "conversation.item.create",
  "item": {
    "type": "function_call_output",
    "call_id": "<call_id from event>",
    "output": "<tool result string>"
  }
}
```

1. Trigger continuation: `{ "type": "response.create" }`

This is identical to the existing Azure implementation.

### CancelResponseAsync

```json
{ "type": "response.cancel" }
```

Same as Azure.

### Implementation Checklist

- [ ] Create `GrokRealtimeVoiceProvider : ILlmVoiceProvider, IConfigurable`
- [ ] Config fields: `ApiKey`, `Voice`, `Model`
- [ ] WSS connection with `Authorization: Bearer` header (not `api-key`)
- [ ] `session.update` on connect with Grok model name
- [ ] Handle `response.output_audio.delta` (not `response.audio.delta`)
- [ ] Everything else: copy from `AzureOpenAiRealtimeVoiceProvider`

### Pricing

Flat rate of approximately **$0.05/min**, roughly half the cost of OpenAI Realtime.

-----

## 2. Google Gemini Live API

### Why it requires more work

Gemini Live uses a **proprietary protocol** — not OpenAI-compatible. The event shapes, field names, and
session handshake are all different. The `IVoiceSession` abstraction makes this manageable: you are mapping
a different wire protocol to the same internal contract, not rewriting OpenAgent.

### API Key

Get a key from [aistudio.google.com](https://aistudio.google.com) (Gemini Developer API) or set up a
Google Cloud project for Vertex AI. The Developer API is simpler to start with and has a free tier.

### Connecting

```
WSS endpoint:  wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={API_KEY}
```

Auth is via query string for the Developer API. For Vertex AI / production, use OAuth and a backend proxy.

### Session Initialisation

The **first message** sent after the WebSocket opens must be the session config (no separate `session.update`
event — configuration is inline with the initial handshake):

```json
{
  "setup": {
    "model": "models/gemini-live-2.5-flash-native-audio",
    "generation_config": {
      "response_modalities": ["AUDIO"],
      "speech_config": {
        "voice_config": {
          "prebuilt_voice_config": { "voice_name": "Puck" }
        }
      }
    },
    "system_instruction": {
      "parts": [{ "text": "<your system prompt>" }]
    },
    "tools": []
  }
}
```

The server responds with a `setup_complete` message before it is ready to accept audio.

### Voice Options

Gemini Live supports a set of prebuilt voices. Commonly available names include:
`Puck`, `Charon`, `Kore`, `Fenrir`, `Aoede`, `Leda`, `Orus`, `Zephyr`.
Voice quality differs per language — check the [Google AI Studio voice tester](https://aistudio.google.com)
to find the right fit before committing.

### Audio In — Sending Audio

```json
{
  "realtimeInput": {
    "audio": {
      "realtimeInputAudio": {
        "data": "<base64 PCM16>",
        "mimeType": "audio/pcm;rate=16000"
      }
    }
  }
}
```

Audio frames can be sent continuously — no need to chunk or commit explicitly. The server derives turn
boundaries from VAD automatically.

For text input (useful for testing without audio):

```json
{
  "realtimeInput": {
    "text": "Hello, how are you?"
  }
}
```

### Audio Out — Receiving Events

Server messages arrive as JSON. There is no typed discriminator field at the top level — you must inspect
the presence of keys:

```json
// Audio delta
{
  "serverContent": {
    "modelTurn": {
      "parts": [
        {
          "inlineData": {
            "mimeType": "audio/pcm;rate=24000",
            "data": "<base64 PCM16 at 24kHz>"
          }
        }
      ]
    }
  }
}

// Turn complete
{
  "serverContent": {
    "turnComplete": true
  }
}

// Input transcription
{
  "serverContent": {
    "inputTranscription": { "text": "What is the weather like?" }
  }
}

// Output transcription
{
  "serverContent": {
    "outputTranscription": { "text": "The weather today is..." }
  }
}
```

### Mapping to VoiceEvent / IVoiceSession

|Gemini event                                |Maps to OpenAgent VoiceEvent |Notes                        |
|--------------------------------------------|-----------------------------|-----------------------------|
|`serverContent.modelTurn.parts[].inlineData`|`AudioDelta`                 |Decode base64, PCM16 at 24kHz|
|`serverContent.outputTranscription.text`    |`TranscriptDelta`            |Assistant transcript         |
|`serverContent.inputTranscription.text`     |(log only, or new event type)|User speech transcript       |
|`serverContent.turnComplete = true`         |`SpeechStarted` boundary     |End of model turn            |
|`serverContent.interrupted`                 |Interrupt signal             |User barged in               |
|`toolCall` at root                          |Tool call event              |See below                    |
|`error` at root                             |`SessionError`               |                             |

### Tool Calling

Gemini's tool call flow differs from OpenAI's but the logic is the same:

**Receiving a tool call:**

```json
{
  "toolCall": {
    "functionCalls": [
      {
        "id": "call_abc123",
        "name": "get_weather",
        "args": { "location": "Copenhagen" }
      }
    ]
  }
}
```

**Sending the tool result:**

```json
{
  "toolResponse": {
    "functionResponses": [
      {
        "id": "call_abc123",
        "name": "get_weather",
        "response": {
          "output": { "result": "Overcast, 12°C" }
        }
      }
    ]
  }
}
```

No explicit `response.create` is needed — the model resumes automatically after receiving the tool response.

### Interruption / CancelResponseAsync

Send an activity end signal:

```json
{ "clientContent": { "turns": [], "turnComplete": true } }
```

Or rely on the server-side VAD interruption — when the user speaks over the model, Gemini sends
`serverContent.interrupted = true` and stops generating.

### Session Limits

Gemini Live sessions have a **maximum duration of ~15 minutes** per WebSocket connection.
You must handle reconnection in `VoiceSessionManager` and re-seed context after reconnecting.
This is a meaningful operational difference from the Azure/Grok providers which have no hard session cap.

Plan for:

- Detecting the `goAway` server event (advance warning of session termination)
- Graceful reconnect with context summary injected as the new session's system instruction

### Audio Format Note

Gemini outputs audio at **24kHz PCM16**. If your client-side audio pipeline is tuned for the 16kHz or 8kHz
output from the Azure provider, you will need to adjust the playback sample rate or resample in your
`SendAudioAsync` / playback path.

### Implementation Checklist

- [ ] Create `GeminiLiveVoiceProvider : ILlmVoiceProvider, IConfigurable`
- [ ] Config fields: `ApiKey`, `Model`, `Voice`, `MaxSessionMinutes`
- [ ] WSS connection with `?key=` query param
- [ ] Send `setup` message on connect; wait for `setup_complete` before accepting audio
- [ ] Implement `ReceiveEventsAsync()` with key-presence inspection (no top-level type discriminator)
- [ ] Map `inlineData` parts → `AudioDelta`
- [ ] Map `toolCall` → tool loop via `IAgentLogic.ExecuteToolAsync()`, send `toolResponse`
- [ ] Handle `goAway` → reconnect with summarised context
- [ ] Confirm 24kHz output sample rate in your audio pipeline

### Pricing

Token-based. Approximately **$1.00 per 1M audio input tokens**, **$3.00 per 1M text output tokens**.
Audio output pricing depends on Vertex AI vs Developer API tier. Free tier available for development
on the Developer API (Google AI Studio key).

-----

## Recommended Integration Order

1. **Start with Grok.** The protocol compatibility means you will have a working second provider in a
   day or two, with minimal risk. It also immediately validates that your `ILlmVoiceProvider` abstraction
   is genuinely provider-agnostic.
1. **Follow with Gemini Live.** More effort, but adds genuine model diversity and gives you a fallback
   if xAI pricing or reliability becomes a concern. The 15-minute session limit is the main operational
   wrinkle to solve.
1. **Wire both into the config system** using the existing keyed singleton + `IConfigurable` pattern,
   same as the text providers. Voice provider selection can then be changed at runtime via the
   Settings app without restart.

-----

## References

- Grok Voice Agent Docs: https://docs.x.ai/docs/guides/voice
- Grok Realtime API Reference: https://docs.x.ai/developers/model-capabilities/audio/voice-agent
- Gemini Live API Overview: https://ai.google.dev/gemini-api/docs/live-api
- Gemini Live API Reference: https://ai.google.dev/api/live
- Gemini Live WebSocket Guide: https://ai.google.dev/gemini-api/docs/live-api/get-started-websocket
