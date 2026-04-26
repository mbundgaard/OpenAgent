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
- Agent-initiated hangup via `EndCallTool` (sets a `pendingHangup` flag; bridge issues `HangupAsync` on next `AudioDone` so the farewell audio plays out).
- Thinking-clip pushed into the Telnyx WebSocket while a tool is executing; analogous browser-side cue triggered from the same provider event.
- ED25519 webhook signature verification (300 s replay window).
- Caller allowlist (empty list = allow all, exact E.164 match otherwise).

**Out:**

- DTMF input (frames are still parsed but ignored).
- Outbound calls (the `IOutboundSender` analogue stays unimplemented for this channel).
- IVR / in-call conversation switching menu.
- App-side concurrency gating — Telnyx's `inbound.channel_limit` (currently 1) is the only enforcement.
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
| `TelnyxCallControlClient.cs` | Three actions wrapping the Telnyx Call Control REST API: `AnswerAsync(callControlId)`, `StreamingStartAsync(callControlId, wsUrl)`, `HangupAsync(callControlId)`. Bearer-token auth from `TelnyxOptions.ApiKey`. **`HangupAsync` is best-effort and idempotent: 404 (call already ended) and 410 (call control id no longer valid) are logged at debug and treated as success.** Multiple teardown paths (caller WS close, agent `EndCallTool`, eviction callback, `SessionError`) can race against an already-hung-up call. |
| `TelnyxWebhookEndpoints.cs` | HTTP routes for call lifecycle webhooks: `POST /api/webhook/telnyx/{webhookId}/call`. Single endpoint dispatches by `event_type`: `call.initiated`, `call.hangup`, `streaming.started`, `streaming.stopped`, `streaming.failed`. |
| `TelnyxStreamingEndpoint.cs` | WebSocket route at `/api/webhook/telnyx/{webhookId}/stream`. Accepts the upgrade, parses `?call={callControlId}` query string, looks up the pending bridge, hands the WS to `TelnyxMediaBridge`. |
| `TelnyxMediaBridge.cs` | Per-call lifetime. Owns the `IVoiceSession`. Read loop on Telnyx WS → base64-decode → `session.SendAudioAsync`. Provider-event loop → base64-encode → outbound media frame. Handles barge-in, tool-call thinking clip, hangup, agent-initiated hangup via flag. |
| `TelnyxMediaFrame.cs` | Record types for the JSON envelopes Telnyx sends and we send: `MediaStart`, `Media`, `MediaStop`, `Dtmf` (parsed but ignored), `Clear` (outbound). |
| `EndCallTool.cs` | `ITool` implementation registered for `ConversationType.Phone` conversations. Sets `(pendingHangup, deadline=now+5s, audioObservedSinceFlag=false)` on the active bridge and returns immediately with `"ok"`. The bridge's hangup decision is **time-bounded** so the model cannot strand the call by emitting its farewell before the tool call (see §"Agent-initiated hangup state machine"). Returns an error result when invoked outside a `ConversationType.Phone` conversation, or when no active bridge exists for the conversation. |
| `ThinkingClipFactory.cs` | Generates the default seamless-loop thinking clip procedurally at provider start: ~2 s of band-limited soft pink noise (300–1000 Hz) encoded as 8 kHz µ-law, with a ~50 ms cosine fade across the loop boundary so repeats are click-free. No third-party asset, no licensing concern. Custom clips override via `TelnyxOptions.ThinkingClipPath` — caller is responsible for loop seamlessness. |

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
- **Wire mapping.** `VoiceSessionOptions.Codec` maps to OpenAI Realtime's `session.update.input_audio_format` and `output_audio_format` fields with the literal values `"pcm16"`, `"g711_ulaw"`, `"g711_alaw"`. Grok mirrors the same field names. `SampleRate` is implicit per codec on these providers (g711_* → 8000, pcm16 → 24000) so we validate it matches and reject the session otherwise rather than silently downgrading.

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
2. `TelnyxWebhookEndpoints` reads body, verifies signature, looks up the running `TelnyxChannelProvider` by `webhookId`. Reject if not found, signature invalid, or timestamp outside 300 s. **Latency budget for the synchronous path: ~200–500 ms typical (verify signature, DB upsert, two REST calls to Telnyx). Telnyx allows up to 10 s before retry — well inside.** The handler stays synchronous; `Answer` and `StreamingStart` complete before we return 200. Simpler than fire-and-forget and avoids races where the WS opens before `_pending` is populated.
3. `From` checked against `AllowedNumbers`. Denied callers get an immediate `HangupAsync(callControlId)` and a 200 response.
4. `FindOrCreateChannelConversation("telnyx", connectionId, From, ConversationType.Phone, voiceProvider, voiceModel)` — same caller, same conversation across calls. `DisplayName` set to the E.164 if missing.
5. `TelnyxCallControlClient.AnswerAsync(callControlId)` (Telnyx now picks up the line — the caller stops hearing ringing).
6. **Register the pending bridge first.** The provider stores a `{callControlId → PendingBridge(conversation, voiceProvider)}` entry in an in-memory dictionary. TTL ~30 s. This MUST happen before `streaming_start` because Telnyx may open the WS before that REST call returns.
7. `TelnyxCallControlClient.StreamingStartAsync(callControlId, wsUrl)` — `wsUrl = "wss://{baseUrl-host}/api/webhook/telnyx/{webhookId}/stream?call={callControlId}"`. Streaming params: `stream_bidirectional_mode=rtp`, `stream_bidirectional_codec=PCMU`, `stream_bidirectional_sampling_rate=8000`, `stream_bidirectional_target_legs=self` (single inbound leg; revisit only if we add transfer/conference), `stream_track=inbound_track` (only the caller's audio is delivered to us — we don't want our own outbound echoed back). `client_state` is set to a base64 of `callControlId` as a redundant correlation channel. Despite the mode name "rtp", Telnyx wraps payloads in JSON envelopes over the WS — no actual RTP framing on our side.

   **Failure paths.** If `streaming_start` returns 5xx or the HTTP call throws, the provider calls `HangupAsync(callControlId)` and removes the pending entry — an answered call without a media bridge would be silent and would burn line-time. If Telnyx never opens the WS within TTL, the eviction callback (see §"Per-call ephemeral state") fires the same `HangupAsync` for the same reason.
8. Telnyx opens the WebSocket. `TelnyxStreamingEndpoint` extracts `?call={callControlId}`, dequeues the pending bridge, calls `bridge.RunAsync(webSocket)`. (`client_state` from the inbound `start` event is checked as a sanity match.)
9. `TelnyxMediaBridge.RunAsync`:
   - Resolves the voice provider via `Func<string, ILlmVoiceProvider>` keyed by `conversation.Provider`.
   - `var session = await provider.StartSessionAsync(conversation, new VoiceSessionOptions("g711_ulaw", 8000), ct);`.
   - Spawns `ReadLoop` (Telnyx → session) and `WriteLoop` (session → Telnyx) and a `ThinkingPump` task (idle by default).
10. **Read loop:** parse JSON envelope. On `start` event capture sample rate / encoding for sanity. On `media` event, ignore the frame if `media.track != "inbound"` (defensive — `stream_track=inbound_track` should already prevent outbound frames, but the filter makes self-feedback impossible if the parameter is misset). Otherwise base64-decode `media.payload`, call `session.SendAudioAsync(bytes)`. On `stop` close. On `dtmf` log + ignore (day one). On unknown event log.
11. **Write loop:** iterate `session.ReceiveEventsAsync()`:
    - `AudioDelta audio`: base64-encode, send `{"event":"media","media":{"payload":"<base64>"}}` over WS.
    - `SpeechStarted`: barge-in. Send `{"event":"clear"}` over WS, call `session.CancelResponseAsync()`.
    - `ToolCallEvent toolCall`: signal `ThinkingPump` to start.
    - `ToolResultEvent`: signal `ThinkingPump` to stop. Send `{"event":"clear"}` to flush any unplayed clip frames before the LLM resumes.
    - `TranscriptDelta` / `TranscriptDone`: forwarded to the conversation (existing voice provider already persists transcripts as messages — bridge is just a passthrough).
    - `SessionError err`: log; bridge will tear down the call via `HangupAsync`.
    - `SessionReady ready`: ignored on Telnyx (codec is fixed); on browser path we still emit it.
    - `AudioDelta`: if `pendingHangup` is set, mark `audioObservedSinceFlag=true` so the next `AudioDone` is treated as the farewell completion.
    - `AudioDone`: if `pendingHangup` is set AND `audioObservedSinceFlag` is true, call `TelnyxCallControlClient.HangupAsync(callControlId)` and clear the flag. Otherwise no-op (the AudioDone belongs to ordinary mid-call audio, or the farewell has not started yet).
12. **ThinkingPump:** when active, repeatedly write base64-encoded chunks of the procedurally-generated thinking clip (or the per-connection override) to the WS at ~20 ms cadence, looping from start when end reached. When deactivated, the `clear` event from the write loop discards anything queued by Telnyx.
13. **Hangup (caller).** Telnyx closes the WS and fires `call.hangup`. Either signal disposes the session and exits both loops. Pending bridge entry (if any) is removed.
14. **Hangup (agent).** The `end_call` tool calls `TelnyxCallControlClient.HangupAsync(callControlId)`. Telnyx tears down the call, fires `call.hangup`, the bridge unwinds as in 13.

## Configuration surface

`TelnyxOptions.ConfigFields` (settings UI):

| Key | Label | Type | Required | Notes |
|---|---|---|---|---|
| `apiKey` | Telnyx API Key | Secret | Yes | v2 API key, used as `Authorization: Bearer …`. |
| `phoneNumber` | Phone Number (E.164) | String | Yes | The number this connection owns; cosmetic but validated as E.164. |
| `baseUrl` | Public Base URL | String | Yes | HTTPS URL of this OpenAgent instance; webhook + WS URLs derive from it. |
| `callControlAppId` | Call Control App ID | String | Yes | Telnyx connection ID of the **Voice / Call Control** connection routing the number. Used by `TelnyxWebhookEndpoints` to validate that incoming events' `payload.connection_id` matches — rejects events from other Call Control connections that share the same OpenAgent instance. |
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

- Default clip is **generated procedurally** at provider start by `ThinkingClipFactory.Generate()`: ~2 s of band-limited soft pink noise (300–1000 Hz), encoded as 8 kHz µ-law mono, with a ~50 ms cosine fade across the loop boundary so repeats are click-free. No third-party audio asset, no licensing concern, no embedded resource needed.
- Custom clip can be supplied per connection via `thinkingClipPath` (relative to `dataPath`). Validated on connection start: must exist, must be readable, must be a multiple of the µ-law frame size. Caller is responsible for loop seamlessness — the bridge does not apply fades to user-provided audio.
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
- **Provider/Model immutability gotcha.** Returning callers continue with whatever `Provider`/`Model` was on their conversation row when first created. Changing `AgentConfig.VoiceProvider` / `VoiceModel` in Settings only affects E.164 numbers calling for the **first** time after the change. Existing callers stay on their original setup. To migrate a returning caller, the agent (or a manual `set_model` call) has to update that conversation explicitly.
- Persisted Messages are transcripts (user transcribed by the Realtime API, assistant transcribed by the same path). The voice provider's existing `TranscriptDone` handling already writes these — bridge is a passthrough.
- New calls inherit prior history because the conversation is E.164-keyed; the Realtime session is initialised with the prior `Message` rows replayed as `conversation.item.create` events at session start (existing voice provider behaviour, not new).
- **Compaction policy.** Phone conversations follow the same post-turn compaction policy as Voice and Text — compaction operates on the conversation, not the channel. Once `LastPromptTokens` exceeds the configured threshold, the next turn triggers compaction. This bounds the replay cost over time, so the deferral of pre-replay compaction (next section) doesn't accumulate forever.

## Allowlist + signature verification

- Verifier: copy/port `TelnyxSignatureVerifier` from the frozen P2 branch unchanged. ED25519 PEM key, 300 s replay window, skip-with-warning when key blank.
- Allowlist: enforced inside the `call.initiated` handler before calling `AnswerAsync`. Denied → `HangupAsync(callControlId)` immediately; bridge is never created.

## Per-call ephemeral state

- The provider keeps two `ConcurrentDictionary` instances, both keyed by `callControlId`:
  - `_pending: PendingBridge` (waiting for Telnyx to open the WS)
  - `_active: TelnyxMediaBridge` (WS open, bridge running) — used by `EndCallTool` to find the bridge for the conversation.
- **Pending eviction.** Each pending entry owns a `CancellationTokenSource` with `CancelAfter(TimeSpan.FromSeconds(30))` whose registered callback removes the entry from `_pending` AND issues `HangupAsync(callControlId)` (a stranded answered call must not stay open). When the WS connects normally, `TelnyxStreamingEndpoint` disposes the CTS and removes the entry atomically before invoking `bridge.RunAsync` — promotes the entry from `_pending` to `_active`.
- **Active eviction.** Active bridges remove themselves from `_active` in their `RunAsync` finally block. They keep their own state (session, conversation, WS, thinking-pump cancellation, pendingHangup flag); once `RunAsync` returns all per-call state is GC'd.
- The `EndCallTool` looks up the active bridge by `conversation.Id` (one bridge per conversation at most). Tool returns an error if no active bridge exists or conversation is not `Phone`.
- One Telnyx connection's `inbound.channel_limit` is enforced **on Telnyx's side** (today: 1 concurrent call). The OpenAgent host does not impose its own gate; the dictionaries above are sized for whatever Telnyx allows through.

## WebSocket endpoint trust model

- ED25519 signing applies to HTTP webhooks only — the WS upgrade carries no signature.
- Two-factor authorisation:
  1. `webhookId` in the URL path — 12-hex random, generated server-side, persisted to `connections.json`. Treat as a shared secret. Never log full webhook URLs (truncate to `…/{first-4}…/stream`).
  2. `?call={callControlId}` query param must match an entry in `_pending`. The 30 s window means an attacker who learned `webhookId` would also have to win the race against Telnyx for any specific incoming call.
- Threat accepted: leaked `webhookId` + race-win attaches an unauthorized WS to an in-flight call. Mitigation is operational (rotate webhook URLs by regenerating `webhookId`; HTTPS-only transport for the REST API tokens that carry the URL). Not a target for cryptographic mitigation in v1.
- **Trust boundary.** The `webhookId` is persisted in `connections.json` alongside the Telnyx API key, the ED25519 public key, and other channel credentials. Anyone with read access to the host filesystem already holds those secrets — `webhookId` is no different. The threat model bracket is "host filesystem read"; further isolation is out of scope.

## Agent-initiated hangup state machine

The naive "set a flag, hang up on next AudioDone" approach is fragile because Realtime APIs may emit the farewell `AudioDone` **before** the model issues the function call (model-determined item ordering). If the model says *"Goodbye"* and only then calls `end_call`, the AudioDone we wanted to catch has already passed and the call would hang until the caller gives up.

The bridge therefore runs a small state machine when `pendingHangup` is set by `EndCallTool`:

```
State at flag-set: { pendingHangup=true, deadline=now+5s, audioObservedSinceFlag=false }

On AudioDelta:
  if pendingHangup: audioObservedSinceFlag = true

On AudioDone:
  if pendingHangup and audioObservedSinceFlag:
    HangupAsync; clear flag                    // farewell played and finished

Background timer (single Task spawned with the flag):
  await Task.Delay(500ms)
  if pendingHangup and not audioObservedSinceFlag:
    HangupAsync; clear flag                    // no farewell in flight, hang up now

  await Task.Delay(remaining_until_deadline)
  if pendingHangup:
    HangupAsync; clear flag                    // hard fallback: model is misbehaving
```

This guarantees the call ends within 5 s of the tool firing, regardless of the model's item ordering or whether it produced any farewell audio at all. The 500 ms early-exit handles the common case where `end_call` is the only thing the model emitted (no parting sentence) — we don't make the caller sit through 5 s of dead air.

## Cancellation during tool execution

- `TelnyxMediaBridge` owns a `CancellationTokenSource` linked to the WS lifecycle. Both the `IVoiceSession` and any tool calls dispatched through `agentLogic.ExecuteToolAsync` receive a token derived from this CTS.
- On `call.hangup` webhook OR WS close, the CTS is cancelled. Tools that honour the token abort.
- `ITool.ExecuteAsync` already accepts a `CancellationToken` (no contract change needed). However, **existing tool implementations need an audit pass** to verify they actually pass it through to network/process work. At minimum: `WebFetchTool` (HTTP requests), `ShellExecTool` (process kill), `MemoryIndex` search calls, anything calling LLM/embedding APIs. File-system tools (`FileReadTool`, `FileWriteTool`, etc.) generally complete before cancellation is observable — acceptable. The implementation plan for this spec includes the audit + targeted fixes.
- Partial transcripts already emitted via `TranscriptDelta` are persisted by the existing voice provider's transcript handler. Transcripts in flight at hangup are best-effort and may be lost — accepted for v1, since call recording is out of scope and the conversation history is reconstructible from completed turns.

## Conversation-history replay cost

- The Realtime session is initialised with prior `Message` rows replayed as `conversation.item.create` events at session start (existing voice provider behaviour, unchanged).
- Compaction does not run pre-replay — `LastPromptTokens` is unset at session-init. The first turn of each call therefore pays the un-compacted token cost of the entire phone conversation history.
- For long-running E.164 conversations this becomes meaningful (many days of accumulated turns). **Accepted for v1.** Follow-ups: (a) trim replayed history to last N messages, (b) summarise via the existing compaction summariser before replay, (c) store a per-conversation "voice context" digest separate from the full message history. Defer until measurement shows it matters.

## Browser thinking-cue protocol

The bridge emits two new control events on the existing browser voice WS, alongside the current `transcript_delta`, `speech_started`, etc.:

```jsonc
{ "type": "thinking_started" }   // emitted on ToolCallEvent
{ "type": "thinking_stopped" }   // emitted on ToolResultEvent
```

Schema additions:

- `OpenAgent.Models/Voice/VoiceWebSocketEvents.cs` (existing file) — add two record types `VoiceThinkingStartedEvent` and `VoiceThinkingStoppedEvent` with `Type` only.
- `OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs` — write loop adds two cases for the new provider-side events (or, more cleanly, emits them from `TelnyxMediaBridge`'s symmetric counterpart in the browser path; spec leaves implementation to the plan).
- React side handles them in the voice client by playing/stopping a local audio file (`web/public/thinking.mp3` or similar). Out of this spec — the frontend change is its own follow-up plan once the protocol is agreed.

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
    ThinkingClipFactory.cs
    EndCallTool.cs
  OpenAgent.Contracts/
    ILlmVoiceProvider.cs                  (modified — VoiceSessionOptions param)
  OpenAgent.Models/
    Conversations/Conversation.cs         (modified — Phone enum)
    Voice/VoiceSessionOptions.cs          (new)
    Voice/VoiceWebSocketEvents.cs         (modified — add ThinkingStarted/ThinkingStopped events)
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
    TelnyxMediaBridgeReadLoopTests.cs       (track filter, dtmf-ignored, base64 decode)
    TelnyxMediaBridgeWriteLoopTests.cs      (audio→base64→envelope, transcripts forwarded)
    TelnyxMediaBridgeBargeInTests.cs        (SpeechStarted → clear + cancel)
    TelnyxMediaBridgeThinkingPumpTests.cs   (ToolCallEvent starts pump, ToolResult stops + clear)
    TelnyxMediaBridgeHangupTests.cs         (caller hangup, agent end_call, AudioDone-then-hangup)
    TelnyxEndCallToolTests.cs               (phone-only gating, pendingHangup flag)
    ThinkingClipFactoryTests.cs             (procedural generation, frame size, fade boundary)
    Fakes/FakeVoiceSession.cs              (new — for bridge tests)
```

## Testing strategy

- **Unit (no WS / no host):**
  - `TelnyxSignatureVerifierTests` (ported from P2): valid sig, wrong sig, expired timestamp, missing key (skip-with-warning), malformed PEM.
  - `TelnyxMediaFrame` parse/serialise roundtrip: each event type, base64 boundaries.
  - `TelnyxCallControlClientTests` against a stub `HttpMessageHandler`: success, 5xx → exception, network error → exception. Verify exact request body for `streaming_start`.
  - `ThinkingClipFactoryTests`: deterministic-seed generation produces clip of expected frame count, fade math, valid µ-law byte distribution.
- **Bridge unit (no WS, with `FakeVoiceSession`):** split per behaviour so coverage is auditable —
  - `TelnyxMediaBridgeReadLoopTests` — incoming `media` framing, base64 decode, `track="outbound"` filter (must be ignored), `dtmf` parsed-and-ignored, malformed JSON tolerated.
  - `TelnyxMediaBridgeWriteLoopTests` — `AudioDelta` → outbound `media` envelope, `TranscriptDelta`/`TranscriptDone` forwarded to the conversation store passthrough, `SessionError` triggers hangup.
  - `TelnyxMediaBridgeBargeInTests` — `SpeechStarted` triggers outbound `clear` AND `session.CancelResponseAsync()` exactly once.
  - `TelnyxMediaBridgeThinkingPumpTests` — `ToolCallEvent` starts the pump (frames begin flowing), `ToolResultEvent` stops the pump and emits `clear`, multiple back-to-back tool calls keep the pump active across them.
  - `TelnyxMediaBridgeHangupTests` — caller-initiated WS close disposes session, agent-initiated `pendingHangup` flag waits for `AudioDone` before invoking `HangupAsync`, `call.hangup` webhook arriving mid-tool cancels the linked CTS.
  - `TelnyxEndCallToolTests` — non-phone conversation returns error result without touching the bridge, phone conversation with no active bridge returns error, phone conversation with active bridge sets the flag and returns OK.
- **Integration (with `WebApplicationFactory<Program>`):**
  - `TelnyxWebhookEndpointTests` — `call.initiated` triggers `Answer` then `StreamingStart` against a fake `TelnyxCallControlClient`; `call.hangup` triggers bridge teardown; signature failures return 401; `connection_id` mismatch returns 401.
  - `TelnyxStreamingEndpointTests` — drive WS frames end-to-end: send `start`+`media` JSON envelopes, assert µ-law bytes reach `FakeVoiceSession.SendAudioAsync`; emit `AudioDelta` from the fake, assert outbound `media` envelopes are received with correct base64; `?call=` mismatch closes the WS.
- **End-to-end manual:** existing Telnyx connection (number `+4535150636`, ED25519 key copied from the portal, allowlist set to the user's caller number), local devtunnel forwarding HTTPS+WSS. Real calls placed; verify barge-in, thinking clip during a `web_fetch` tool call, agent-initiated hangup via `end_call`, full transcript persisted in the conversation store.

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
- `stream_track=inbound_track` causes Telnyx to deliver only the caller's audio to our bridge (no echo of our outbound audio back to us). Confirmed from Telnyx OpenAPI `StreamTrack` enum and the `media.track` field shown on inbound media frames in the developer docs. The defensive read-loop filter (`media.track != "inbound"` → skip) is belt-and-braces.
- Telnyx delivers the `start` event with `media_format` populated; we treat URL `?call={callControlId}` as the primary correlation key and validate against `start.media_format` only for sanity logging.
- `webhookPublicKey` is the same key used to sign both call-lifecycle webhooks and streaming-event webhooks (verified per Telnyx Developer Hub UI).
- Tool execution inside Realtime providers happens off the audio thread — the thinking-pump is purely cosmetic; we don't rely on it to throttle real work.

## Out-of-spec things that may still need a small follow-up

- A `SwitchConversationTool` would be the primitive behind the future "in-call menu" feature. The bridge's per-call state model leaves room: swapping `bridge.Conversation` and re-initialising the session is mechanically possible. **But there is an open conversation-model question** — phone conversations are deterministically E.164-keyed. "Switch" must be defined: pick a different existing E.164 conversation? Spawn a non-E.164-keyed ephemeral one (and then how does it persist across calls)? Drop the E.164 keying as a hard rule and add explicit conversation IDs to phone? The follow-up needs that decision before the tool can be designed.
- Concurrency >1 — no code change should be needed; Telnyx-side `inbound.channel_limit` is the only gate, and the in-memory pending-bridge dictionary is already keyed per call.
