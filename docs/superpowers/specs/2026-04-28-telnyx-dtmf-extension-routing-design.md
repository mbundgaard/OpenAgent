# Telnyx DTMF Extension Routing — Design

**Date:** 2026-04-28
**Status:** Approved, pending implementation plan

## Problem

Today every inbound Telnyx call from a given caller number lands in a single conversation keyed on `from`. There is no way to address a specific persona, conversation thread, or context from one phone line — every call to `+4535150636` shares the same history, regardless of intent.

We want callers to be able to dial an extension via the standard phone-keypad pattern (`+4535150636,1`, where `,` is a comma-pause that auto-sends DTMF after answer) and route into a different conversation per extension. Without delaying the agent's greeting on every call.

## Non-Goals

- **Pre-answer extension info.** PSTN does not carry it; SIP-trunk-only mechanisms are out of scope.
- **DTMF menus / IVR prompts.** No "press 1 for X, 2 for Y" — caller already knows their extension.
- **Multi-digit extensions.** First digit wins; subsequent DTMF in the same call is ignored.
- **Mid-call extension changes.** The DTMF gate closes after a fixed window; later DTMF is logged but not acted on.
- **Cross-call continuity for non-extension callers.** A call without DTMF creates its own per-call conversation. Repeat callers without DTMF do not merge into a single thread.
- **Gemini Live support.** Gemini's protocol does not support clean mid-flight system-prompt swap; DTMF on Gemini is logged and ignored.

## Design

### Conversation lifecycle

**On `call.initiated`** — answer the call, start streaming, register a pending bridge. Do **not** look up or create a conversation yet. The previous behaviour of `FindOrCreateChannelConversation(channelChatId: from)` moves out of the webhook handler and into the bridge.

**On WS connect** — the media bridge:

1. Creates a fresh per-call `Conversation` (the *throwaway*) with `channelChatId = "{from}:{call_session_id}"`. This is unique per call, so each call is its own conversation.
2. Brings up the realtime session bound to the throwaway, base system prompt (no skills, no intention, no prior history — there is none yet).
3. Sends the synthetic greeting trigger; the agent starts speaking.
4. Opens an 8-second DTMF gate, measured from `call.initiated`.

**During the gate** — the first DTMF digit triggers a swap:

1. Resolve `extension = FindOrCreateChannelConversation(channelChatId: "{from},{digit}")`.
2. Acquire the bridge's swap lock.
3. `session.update` with `extension`'s system prompt (built from active skills, intention, summary).
4. `conversation.item.create` for each persisted message in `extension`, in chronological order. History injection lands after the greeting in the realtime session's view; the model is expected to handle the slight ordering wart.
5. Update the bridge's `_conversation` reference to `extension`. Subsequent persisted assistant/user turns now land on `extension`.
6. Delete the throwaway conversation. The greeting message persisted on the throwaway is lost — accepted, since it is short and identity-neutral.
7. Release the swap lock and close the gate (single-shot — subsequent DTMF in the gate window is ignored).

**Gate expires (no DTMF in 8 s)** — close the gate; the throwaway continues as the call's permanent conversation. No swap, no delete, no merge.

**Mid-call DTMF (>8 s)** — logged, not acted on.

**Hangup** — normal cleanup; both throwaway and extension conversations behave identically post-call (just rows in the store).

### Display name disambiguation

Throwaway and extension conversations both have the same caller number. To disambiguate in the UI:

- Throwaway: `DisplayName = from` (e.g. `+4551504261`)
- Extension: `DisplayName = "{from} ext.{digit}"` (e.g. `+4551504261 ext.1`)

### IVoiceSession contract change

Add a new method to `IVoiceSession`:

```csharp
Task RebindConversationAsync(Conversation newConversation, CancellationToken ct = default);
```

Behaviour:

1. Update the session's internal `_conversation` reference.
2. Re-fetch the live conversation (`agentLogic.GetConversation(newConversation.Id) ?? newConversation`) and rebuild the system prompt; push via `session.update`.
3. Inject prior messages from `newConversation.GetMessages(includeToolResultBlobs: true)` via `conversation.item.create` (skip tool-result-only messages, same filter the initial seed uses today).

Provider matrix:

| Provider | Implementation |
|---|---|
| OpenAI Realtime (`AzureOpenAiVoiceSession`) | Full support via `session.update` + `conversation.item.create` |
| Grok Realtime (`GrokVoiceSession`) | Mirrors OpenAI — full support |
| Gemini Live (`GeminiLiveVoiceSession`) | Throws `NotSupportedException` |

The Telnyx bridge catches `NotSupportedException` and degrades gracefully: DTMF is logged, swap is skipped, the throwaway just continues as the call's conversation.

### DTMF delivery path

`call.dtmf.received` arrives via the existing webhook endpoint. New case in `TelnyxWebhookEndpoints.HandleCallEvent`:

```csharp
case "call.dtmf.received" => OnDtmfReceived(provider, env, ct),
```

`OnDtmfReceived`:

1. Look up the bridge (or pending entry) by `call_control_id`.
2. If pending (WS not yet connected) — enqueue the digit on `PendingBridge.PendingDtmf` (a new `ConcurrentQueue<string>`).
3. If active — call `bridge.OnDtmfReceived(digit)` which delivers via the bridge's internal queue/channel.
4. Return `Results.Ok()` immediately. Swap work happens on the bridge thread, not in the webhook handler.

### Bridge state additions

`TelnyxMediaBridge` gains:

- `SemaphoreSlim _swapLock = new(1, 1)`
- `bool _gateOpen` (set true on bridge start, cleared after first digit OR after 8 s)
- `DateTimeOffset _gateOpenedAt`
- `ChannelReader<string> _dtmfReader` / `ChannelWriter<string> _dtmfWriter` (single-reader queue from webhook handler to bridge loop)

Bridge startup drains any digits queued on `PendingBridge.PendingDtmf` before opening the gate. If a digit was queued pre-WS, the swap kicks off immediately.

The swap lock also wraps message persistence — the realtime session's `response.done` handlers and tool-result writers acquire it briefly before persisting to `_conversation`. This serialises the pointer flip with persistence: any persist that wins the lock first writes to throwaway (then throwaway is deleted, message lost), any persist that wins after the swap writes to extension. This is the accepted behaviour from the lifecycle section.

### Connection registry

`TelnyxBridgeRegistry` already keys active bridges on `call_control_id`. Add `TryGetPending(callControlId, out PendingBridge)` for the webhook handler's pre-WS path.

## Open questions resolved

- **DTMF gate window**: 8 seconds from `call.initiated` (covers the typical ~3 s comma-pause + buffer for slow phones)
- **Multi-digit**: single-shot, first digit wins
- **Greeting persistence on swap**: lost (deleted with throwaway)
- **History ordering on swap**: best-effort, model handles it
- **Non-extension call continuity**: each call is a new conversation (no `from`-keyed merge)
- **Gemini Live**: not supported, DTMF ignored

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| DTMF arrives before WS connects | Buffer on `PendingBridge.PendingDtmf`, drain on bridge start |
| Persist race during swap (response.done lands during pointer flip) | `SemaphoreSlim` serialises swap and persist; late writes pre-flip are lost (intentional) |
| Throwaway orphaned if WS never connects | Existing pending-bridge 30 s timeout already hangs up the call; throwaway not yet created at that point |
| Throwaway orphaned if agent crashes mid-call | Same as today's `from`-keyed conversation behaviour — no regression |
| Telnyx redelivers DTMF webhook | Webhook handler returns `Results.Ok()` immediately; bridge gate is single-shot — duplicate digit is a no-op |
| `session.update` or `conversation.item.create` fails mid-swap | Catch and log; restore `_conversation` to throwaway; throwaway continues as the conversation. DTMF acts as a no-op in this case. |

## Out of scope (deferred)

- Multi-digit extensions
- DID-per-extension (alternate routing strategy, no DTMF needed)
- DTMF as in-call tool input (`[user pressed 1]` mid-conversation)
- UI editor for extension/persona mappings
- Gemini Live support for swap
- Cross-call continuity for non-extension callers (re-keying throwaway to `{from}` on timeout)
