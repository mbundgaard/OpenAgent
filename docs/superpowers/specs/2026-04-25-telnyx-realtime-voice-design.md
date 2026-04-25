# Telnyx Real-Time Voice — Design

> Replaces the prior P2 TeXML work (`feature/telnyx-channel-scaffolding`, frozen on origin) with a Media Streaming bridge that gives caller and agent native real-time voice via an `ILlmVoiceProvider`. The text-based TeXML pipeline is dropped entirely.

## Goal

Inbound phone calls to a Telnyx-owned number stream audio bidirectionally over a WebSocket between Telnyx and our agent. The agent uses the same Realtime LLM session it uses for the browser voice path (same provider, same tools, same conversation history) — only the audio codec and the channel-specific framing differ.

## Scope

**In:**

- Inbound calls only.
- One Telnyx connection / one phone number per channel connection (multiple connections still supported via the existing `IChannelProvider` infrastructure).
- E.164-keyed conversations: caller's number maps to a single ongoing `Conversation` across calls.
- Same tool surface as text channels (web fetch, file ops, scheduled tasks, …) via Realtime function calling. Tools are channel-agnostic.
- Barge-in (caller interrupts agent mid-speech) — server-side VAD plus an outbound `clear` event flushes queued audio.
- Agent-initiated hangup (`end_call` tool issuing a Call Control `hangup` action).
- Thinking-clip pushed into the Telnyx WebSocket while a tool is executing; analogous browser-side cue triggered from the same provider event.
- ED25519 webhook signature verification (300 s replay window).
- Caller allowlist (empty list = allow all, exact E.164 match otherwise).

**Out:**

- DTMF input (frames are still parsed but ignored).
- Outbound calls (the `IOutboundSender` analogue stays unimplemented for this channel).
- IVR / in-call conversation switching menu.
- Concurrent calls beyond the Telnyx-side connection limit (currently 1).
- Call recording.
- Gemini Live phone support — Gemini's hardcoded PCM16 16k/24k cannot reach Telnyx without resampling, which we explicitly chose not to build.

## Architecture

```
                                                           ┌──────────────┐
                                                           │ Telnyx Cloud │
                                                           └──────┬───────┘
                                                                  │
              ┌───────────────────────────────────────────────────┼─────────────────────────────┐
              │ OpenAgent host                                    │                             │
              │                                                   │                             │
              │ 1. Telnyx → POST /api/webhook/telnyx/{wid}/call   │                             │
              │    body: call.initiated, ED25519 signed           │                             │
              │                                                   │                             │
              │ 2. Verify signature                               │                             │
              │ 3. Allowlist check                                │                             │
              │ 4. FindOrCreateChannelConversation(E.164)         │                             │
              │ 5. CallControlClient.Answer(callControlId) ─────► │                             │
              │ 6. Register pending bridge entry                  │                             │
              │ 7. CallControlClient.StreamingStart(             │                             │
              │      callControlId,                               │                             │
              │      url=wss://{us}/api/webhook/telnyx/{wid}      │                             │
              │          /stream?call={callControlId},            │                             │
              │      mode=rtp,                                    │                             │
              │      codec=PCMU, rate=8000,                       │                             │
              │      target_legs=self,                            │                             │
              │      client_state=base64(callControlId)) ───────► │                             │
              │                                                   │                             │
              │                                                   │ 8. Telnyx WS handshake      │
              │                                              ◄────┤    to /api/webhook/telnyx   │
              │                                                   │       /{wid}/stream         │
              │ 9. TelnyxStreamingEndpoint accepts WS             │                             │
              │10. TelnyxMediaBridge starts                       │                             │
              │     ↳ resolves voice provider                     │                             │
              │     ↳ StartSessionAsync(conv,                     │                             │
              │         options=(g711_ulaw, 8000))                │                             │
              │     ↳ session.ReceiveEventsAsync                  │                             │
              │                                                   │                             │
              │  11. media frames flow both ways (µ-law 8k        │                             │
              │      inside JSON envelopes; thinking clip         │                             │
              │      pushed during ToolCallEvent → clear on       │                             │
              │      ToolResultEvent; SpeechStarted triggers      │                             │
              │      clear + CancelResponseAsync for barge-in)    │                             │
              │                                                   │                             │
              │  12. call.hangup OR WS close → bridge disposes    │                             │
              │      session, conversation persisted              │                             │
              └───────────────────────────────────────────────────┴─────────────────────────────┘
```

## Components

### `OpenAgent.Channel.Telnyx` (new project, built from scratch)

| File | Responsibility |
|---|---|
| `TelnyxOptions.cs` | Strongly-typed connection config (see Configuration below). |
| `TelnyxChannelProviderFactory.cs` | `IChannelProviderFactory`. Exposes `Type="telnyx"`, `DisplayName="Telnyx"`, the `ConfigFields` for the dynamic settings UI, and constructs `TelnyxChannelProvider` from a `Connection`. |
| `TelnyxChannelProvider.cs` | `IChannelProvider`. Lifecycle (StartAsync/StopAsync). Holds the `TelnyxCallControlClient`, `TelnyxSignatureVerifier`, `AllowedNumbers`, generated `WebhookId`. Persists `webhookId` to `connections.json` on first start (mirrors P2 behaviour). |
| `TelnyxSignatureVerifier.cs` | ED25519 verification of `Telnyx-Signature-ed25519` + `Telnyx-Timestamp` over `{timestamp}|{rawBody}`. 300 s replay window. Skipped with a warning when `WebhookPublicKey` is blank (dev-only). Uses BouncyCastle. |
| `TelnyxCallControlClient.cs` | Three actions wrapping the Telnyx Call Control REST API: `AnswerAsync(callControlId)`, `StreamingStartAsync(callControlId, wsUrl)`, `HangupAsync(callControlId)`. Bearer-token auth from `TelnyxOptions.ApiKey`. |
| `TelnyxWebhookEndpoints.cs` | HTTP routes for call lifecycle webhooks: `POST /api/webhook/telnyx/{webhookId}/call`. Single endpoint dispatches by `event_type`: `call.initiated`, `call.hangup`, `streaming.started`, `streaming.stopped`, `streaming.failed`. |
| `TelnyxStreamingEndpoint.cs` | WebSocket route at `/api/webhook/telnyx/{webhookId}/stream`. Accepts the upgrade, parses `?call={callControlId}` query string, looks up the pending bridge, hands the WS to `TelnyxMediaBridge`. |
| `TelnyxMediaBridge.cs` | Per-call lifetime. Owns the `IVoiceSession`. Read loop on Telnyx WS → base64-decode → `session.SendAudioAsync`. Provider-event loop → base64-encode → outbound media frame. Handles barge-in, tool-call thinking clip, hangup. |
| `TelnyxMediaFrame.cs` | Record types for the JSON envelopes Telnyx sends and we send: `MediaStart`, `Media`, `MediaStop`, `Dtmf` (parsed but ignored), `Clear` (outbound), `Mark` (outbound, used to detect when the thinking clip has finished playing if needed). |
| `Resources/thinking.ulaw` | Embedded default thinking clip — 8 kHz µ-law mono, 1–3 s, soft ambient. The bridge loops it. Path overridable per-connection via `TelnyxOptions.ThinkingClipPath`. |

### Provider contract changes

`OpenAgent.Models/Voice/VoiceSessionOptions.cs` (new):

```csharp
public sealed record VoiceSessionOptions(string Codec, int SampleRate);
```

`OpenAgent.Contracts/ILlmVoiceProvider.cs` modified:

```csharp
Task<IVoiceSession> StartSessionAsync(
    Conversation conversation,
    VoiceSessionOptions? options = null,
    CancellationToken ct = default);
```

`AzureOpenAiRealtimeVoiceProvider` and `GrokRealtimeVoiceProvider`:

- Drop `codec` and (for Grok) `sampleRate` from `ConfigFields` — channel decides, not user.
- Default to `pcm16 / 24000` internally when `options` is null.
- Honour `VoiceSessionOptions` when provided. The Telnyx bridge passes `("g711_ulaw", 8000)`; the existing browser endpoint passes null and gets the default.
- The `_config.Codec` field is removed from each provider's deserialised config struct; tests asserting it move accordingly.

`GeminiLiveVoiceProvider` is **not modified** — it stays at hardcoded PCM16 16k in / 24k out, no config field. Phone support for Gemini is a future follow-up that requires a resampler.

### `OpenAgent.Models` additions

- `ConversationType.Phone` enum value (re-added relative to master, same shape as P2).
- `defaults/PHONE.md` system prompt template (re-introduced as embedded resource of `OpenAgent` host project, picked up by the existing `DataDirectoryBootstrap` extraction loop).
- `SystemPromptBuilder.FileMap`:
  - `Phone` is included in the channel set for `AGENTS.md`, `SOUL.md`, `IDENTITY.md`, `USER.md`, `TOOLS.md`, `MEMORY.md`.
  - `("PHONE.md", [ConversationType.Phone])` added.

### Host wiring (`OpenAgent/Program.cs`)

- `using OpenAgent.Channel.Telnyx;`
- `builder.Services.AddSingleton<IChannelProviderFactory>(sp => new TelnyxChannelProviderFactory(...))` — same shape as the existing Telegram/WhatsApp registrations. Voice provider resolver is injected as `Func<string, ILlmVoiceProvider>` (introduced as a new keyed-singleton resolver, mirroring the existing text-provider resolver pattern).
- `app.MapTelnyxWebhookEndpoints();`
- `app.MapTelnyxStreamingEndpoint();`

## Data flow: a single phone call

1. Caller dials. PSTN → Telnyx → `call.initiated` webhook.
2. `TelnyxWebhookEndpoints` reads body, verifies signature, looks up the running `TelnyxChannelProvider` by `webhookId`. Reject if not found, signature invalid, or timestamp outside 300 s.
3. `From` checked against `AllowedNumbers`. Denied callers get an immediate `HangupAsync(callControlId)` and a 200 response.
4. `FindOrCreateChannelConversation("telnyx", connectionId, From, ConversationType.Phone, voiceProvider, voiceModel)` — same caller, same conversation across calls. `DisplayName` set to the E.164 if missing.
5. `TelnyxCallControlClient.AnswerAsync(callControlId)` (Telnyx now picks up the line — the caller stops hearing ringing).
6. **Register the pending bridge first.** The provider stores a `{callControlId → PendingBridge(conversation, voiceProvider)}` entry in an in-memory dictionary. TTL ~30 s. This MUST happen before `streaming_start` because Telnyx may open the WS before that REST call returns.
7. `TelnyxCallControlClient.StreamingStartAsync(callControlId, wsUrl)` — `wsUrl = "wss://{baseUrl-host}/api/webhook/telnyx/{webhookId}/stream?call={callControlId}"`. Streaming params: `stream_bidirectional_mode=rtp`, `stream_bidirectional_codec=PCMU`, `stream_bidirectional_sampling_rate=8000`, `stream_bidirectional_target_legs=self` (single inbound leg; revisit only if we add transfer/conference). `client_state` is set to a base64 of `callControlId` as a redundant correlation channel. Despite the mode name "rtp", Telnyx wraps payloads in JSON envelopes over the WS — no actual RTP framing on our side.
8. Telnyx opens the WebSocket. `TelnyxStreamingEndpoint` extracts `?call={callControlId}`, dequeues the pending bridge, calls `bridge.RunAsync(webSocket)`. (`client_state` from the inbound `start` event is checked as a sanity match.)
9. `TelnyxMediaBridge.RunAsync`:
   - Resolves the voice provider via `Func<string, ILlmVoiceProvider>` keyed by `conversation.Provider`.
   - `var session = await provider.StartSessionAsync(conversation, new VoiceSessionOptions("g711_ulaw", 8000), ct);`.
   - Spawns `ReadLoop` (Telnyx → session) and `WriteLoop` (session → Telnyx) and a `ThinkingPump` task (idle by default).
10. **Read loop:** parse JSON envelope. On `start` event capture sample rate / encoding for sanity. On `media` event base64-decode payload, call `session.SendAudioAsync(bytes)`. On `stop` close. On `dtmf` log + ignore (day one). On unknown event log.
11. **Write loop:** iterate `session.ReceiveEventsAsync()`:
    - `AudioDelta audio`: base64-encode, send `{"event":"media","media":{"payload":"<base64>"}}` over WS.
    - `SpeechStarted`: barge-in. Send `{"event":"clear"}` over WS, call `session.CancelResponseAsync()`.
    - `ToolCallEvent toolCall`: signal `ThinkingPump` to start.
    - `ToolResultEvent`: signal `ThinkingPump` to stop. Send `{"event":"clear"}` to flush any unplayed clip frames before the LLM resumes.
    - `TranscriptDelta` / `TranscriptDone`: forwarded to the conversation (existing voice provider already persists transcripts as messages — bridge is just a passthrough).
    - `SessionError err`: log; bridge will tear down the call via `HangupAsync`.
    - `SessionReady ready`: ignored on Telnyx (codec is fixed); on browser path we still emit it.
12. **ThinkingPump:** when active, repeatedly write base64-encoded chunks of `thinking.ulaw` (or the per-connection override) to the WS at ~20 ms cadence, looping from start when end reached. When deactivated, the `clear` event from the write loop discards anything queued by Telnyx.
13. **Hangup (caller).** Telnyx closes the WS and fires `call.hangup`. Either signal disposes the session and exits both loops. Pending bridge entry (if any) is removed.
14. **Hangup (agent).** The `end_call` tool calls `TelnyxCallControlClient.HangupAsync(callControlId)`. Telnyx tears down the call, fires `call.hangup`, the bridge unwinds as in 13.

## Configuration surface

`TelnyxOptions.ConfigFields` (settings UI):

| Key | Label | Type | Required | Notes |
|---|---|---|---|---|
| `apiKey` | Telnyx API Key | Secret | Yes | v2 API key, used as `Authorization: Bearer …`. |
| `phoneNumber` | Phone Number (E.164) | String | Yes | The number this connection owns; cosmetic but validated as E.164. |
| `baseUrl` | Public Base URL | String | Yes | HTTPS URL of this OpenAgent instance; webhook + WS URLs derive from it. |
| `callControlAppId` | Call Control App ID | String | Yes | Telnyx connection ID of the **Voice / Call Control** connection routing the number. |
| `webhookPublicKey` | Webhook Public Key (PEM) | Secret | No | ED25519 PEM. Verification skipped with warning when blank. |
| `allowedNumbers` | Allowed Caller Numbers | String | No | Comma-separated E.164. Empty = allow all. |
| `thinkingClipPath` | Thinking Clip Path | String | No | Optional path under `dataPath` to a custom µ-law 8 kHz mono clip. Falls back to embedded default. |

Auto-generated and persisted invisibly into the connection blob: `webhookId` (12-hex random, namespaces all webhook URLs).

`stream_url` returned to Telnyx on `streaming_start`:
`wss://{host(baseUrl)}/api/webhook/telnyx/{webhookId}/stream?call={callControlId}`.

## Telnyx portal setup (one-time, by user)

1. **Mission Control → Voice → Programmable Voice → Create Connection.**
2. Type: **Call Control** (NOT TeXML).
3. Connection webhook URL: `{baseUrl}/api/webhook/telnyx/{webhookId}/call` (HTTP POST).
4. Webhook API version: `2`.
5. Copy the new connection's ID into `callControlAppId` in OpenAgent's settings UI.
6. Assign the phone number to this Call Control connection.
7. Developer Hub → Webhook Signing — copy the public key into `webhookPublicKey`.

Streaming itself is started programmatically per call via the REST `streaming_start` action; no portal-side streaming config is needed.

## Audio format rule (single source of truth)

| Hop | Codec | Sample Rate |
|---|---|---|
| Telnyx ↔ TelnyxMediaBridge | µ-law (`PCMU` on Telnyx side, `g711_ulaw` on provider side) | 8 000 Hz |
| TelnyxMediaBridge ↔ provider session | µ-law (passed as `VoiceSessionOptions`) | 8 000 Hz |
| Browser ↔ provider session | PCM16 (provider default) | 24 000 Hz |
| Future native app ↔ provider session | PCM16 | 24 000 Hz |

Bridge does **no** codec or rate conversion. Pure byte pipe. Each leg's audio is the codec the next hop natively expects.

## Thinking-clip mechanism

- Default clip embedded as `OpenAgent.Channel.Telnyx/Resources/thinking.ulaw` (8 kHz µ-law mono, 1–3 s, soft ambient — no speech). Loaded once at provider start.
- Custom clip can be supplied per connection via `thinkingClipPath` (relative to `dataPath`). Validated on connection start: must exist, must be readable, must be a multiple of the µ-law frame size.
- Bridge `ThinkingPump`:
  - Idle by default.
  - On `ToolCallEvent`, transitions to active. Writes the clip in 20-ms slices (160 bytes/slice) to the WS at a 20 ms cadence (target one frame per slice; small jitter tolerated).
  - On `ToolResultEvent`, transitions to idle and the write loop sends `{"event":"clear"}` to flush any frames Telnyx hasn't yet played.
  - If the LLM emits multiple tool calls in a row, the pump stays active across them; the `clear` only fires when control returns to spoken response.
- Browser equivalent: bridge emits a synthetic `ThinkingStarted` / `ThinkingStopped` JSON event on the existing WS. Frontend plays a local audio file. Avoids server-side clip processing for the browser case.

## Barge-in

- Realtime APIs already emit `SpeechStarted` events server-side via VAD (the codebase fixed an interruption bug recently in commit `7784031`).
- On `SpeechStarted`, the bridge's write loop sends `{"event":"clear"}` to Telnyx (drops queued outbound audio) and calls `session.CancelResponseAsync()` (stops LLM generation).
- Browser path's existing barge-in logic stays unchanged.

## Conversation history

- `Conversation.Type = ConversationType.Phone` for all calls on this channel.
- `Provider`/`Model` set to the host's configured voice provider/model at conversation creation; per-conversation switch via existing `set_model` tool still works.
- Persisted Messages are transcripts (user transcribed by the Realtime API, assistant transcribed by the same path). The voice provider's existing `TranscriptDone` handling already writes these — bridge is a passthrough.
- New calls inherit prior history because the conversation is E.164-keyed; the Realtime session is initialised with the prior `Message` rows replayed as `conversation.item.create` events at session start (existing voice provider behaviour, not new).

## Allowlist + signature verification

- Verifier: copy/port `TelnyxSignatureVerifier` from the frozen P2 branch unchanged. ED25519 PEM key, 300 s replay window, skip-with-warning when key blank.
- Allowlist: enforced inside the `call.initiated` handler before calling `AnswerAsync`. Denied → `HangupAsync(callControlId)` immediately; bridge is never created.

## Per-call ephemeral state

- The provider keeps a `ConcurrentDictionary<string, PendingBridge>` keyed by `callControlId`. Entries are added when `streaming_start` is issued and removed when either the WS connects or 30 s elapses.
- Active bridges keep their own state (session, conversation, WS, thinking-pump cancellation). They are NOT registered globally — once `RunAsync` returns, all per-call state is GC'd.
- One Telnyx connection's `inbound.channel_limit` defaults to 1. We don't enforce concurrency on our side beyond what the WS lifecycle naturally implies.

## File structure

```
src/agent/
  OpenAgent.Channel.Telnyx/
    OpenAgent.Channel.Telnyx.csproj
    TelnyxOptions.cs
    TelnyxChannelProvider.cs
    TelnyxChannelProviderFactory.cs
    TelnyxSignatureVerifier.cs
    TelnyxCallControlClient.cs
    TelnyxWebhookEndpoints.cs
    TelnyxStreamingEndpoint.cs
    TelnyxMediaBridge.cs
    TelnyxMediaFrame.cs
    Resources/
      thinking.ulaw
  OpenAgent.Contracts/
    ILlmVoiceProvider.cs                  (modified — VoiceSessionOptions param)
  OpenAgent.Models/
    Conversations/Conversation.cs         (modified — Phone enum)
    Voice/VoiceSessionOptions.cs          (new)
  OpenAgent.LlmVoice.OpenAIAzure/
    AzureOpenAiRealtimeVoiceProvider.cs   (modified — drop codec field, honour options)
    AzureOpenAiVoiceSession.cs            (modified — accept format from options)
  OpenAgent.LlmVoice.GrokRealtime/
    GrokRealtimeVoiceProvider.cs          (modified)
    GrokVoiceSession.cs                   (modified)
  OpenAgent/
    Program.cs                             (modified — register Telnyx + map endpoints + voice provider resolver)
    defaults/PHONE.md                      (new)
    SystemPromptBuilder.cs                 (modified — Phone in FileMap)
  OpenAgent.Tests/
    TelnyxSignatureVerifierTests.cs
    TelnyxCallControlClientTests.cs
    TelnyxWebhookEndpointTests.cs
    TelnyxStreamingEndpointTests.cs
    TelnyxMediaBridgeTests.cs
    Fakes/FakeVoiceSession.cs              (new — for bridge tests)
```

## Testing strategy

- **Unit:** `TelnyxSignatureVerifierTests` (ported from P2). `TelnyxMediaFrame` parse/serialise roundtrip. `TelnyxCallControlClient` against a stub `HttpMessageHandler`.
- **Integration:** `TelnyxWebhookEndpointTests` and `TelnyxStreamingEndpointTests` via `WebApplicationFactory<Program>`. For the webhook tests, assert `call.initiated` triggers `Answer` + `StreamingStart` REST calls (against a fake CallControlClient). For the streaming endpoint, drive WS frames through, verify base64 µ-law payloads reach a `FakeVoiceSession`, and that audio events from the session round-trip back into the WS as base64 envelopes.
- **End-to-end manual:** existing Telnyx connection (number `+4535150636`, ED25519 key copied from the portal, allowlist set to the user's caller number), local devtunnel forwarding HTTPS+WSS. Real calls placed; verify barge-in, thinking clip during a tool call (e.g. `web_fetch`), agent-initiated hangup (`end_call` tool).

## What we keep / remove from the prior P2 work

The frozen P2 branch (`feature/telnyx-channel-scaffolding`) is the source for ideas, not code. We start from `master` on `feature/telnyx-realtime-voice` and copy these specific patterns:

- ED25519 verifier logic (lift verbatim).
- `TelnyxOptions` shape (lift, then add `callControlAppId` + `thinkingClipPath`, drop nothing).
- `webhookId` auto-generate-and-persist-on-first-start pattern.
- `PHONE.md` content and `ConversationType.Phone` enum (re-introduce).
- The `IChannelProvider` lifecycle plumbing (already part of the codebase, no copy needed).

Discarded entirely:

- `TeXmlBuilder.cs` — TeXML XML composition is not used.
- `TelnyxMessageHandler.cs` — text-flow turn orchestration is replaced by the audio bridge.
- The old TeXML webhook routes (`voice`, `speech`, `status`).
- The Telnyx portal's TeXML Application — replaced by a Call Control connection.

## Open assumptions (verified or accepted)

- Bidirectional `streaming_start` with `mode=rtp` delivers audio over the same WebSocket as JSON-envelope `media` events with base64-encoded codec payloads (no manual RTP framing on our side). Confirmed from Telnyx OpenAPI spec + their AI-voice-agent docs.
- Telnyx delivers the `start` event with `media_format` populated; we treat URL `?call={callControlId}` as the primary correlation key and validate against `start.media_format` only for sanity logging.
- `webhookPublicKey` is the same key used to sign both call-lifecycle webhooks and streaming-event webhooks (verified per Telnyx Developer Hub UI).
- Tool execution inside Realtime providers happens off the audio thread — the thinking-pump is purely cosmetic; we don't rely on it to throttle real work.

## Out-of-spec things that may still need a small follow-up

- A `SwitchConversationTool` would be the primitive behind the future "in-call menu" feature. Not in this design but the bridge's per-call state model leaves room: swapping `bridge.Conversation` and re-initialising the session is mechanically possible.
- Concurrency >1 — no code change should be needed; Telnyx-side `inbound.channel_limit` is the only gate, and the in-memory pending-bridge dictionary is already keyed per call.
