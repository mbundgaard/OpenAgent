# Chat App — Design Spec

A unified web app that combines text messaging and voice sessions in a single conversation surface, with a sidebar listing all conversations regardless of source.

## Goal

Replace the mental model of "pick a transport, then talk" with "pick a conversation, then talk however you want". The user opens one app, browses conversations across all sources (app/telegram/whatsapp/scheduledtask), selects one (or starts fresh), and freely mixes typed messages with voice exchanges. The conversation history is one timeline regardless of how each message was produced.

The new app coexists with the existing `text`, `voice`, and `conversations` apps — none of those are touched. They can be retired later if this app proves itself.

## Requirements

- **Coexists with existing apps** — `text`, `voice`, and `conversations` are not modified or removed.
- **Single sidebar** — lists all conversations across all sources, sorted by last activity (newest first).
- **Conversation selection on demand** — app opens with no conversation selected; the user clicks an existing one or hits `+ New conversation`.
- **All conversations are continuable** — clicking any conversation (including telegram/whatsapp/scheduledtask) lets the user send a text message or start a voice session against it.
- **Message modality is persisted but not visually distinguished** — Messages gain a `modality` field (`text` or `voice`) for data fidelity, but the chat app renders both identically (user bubbles right, assistant markdown left/center).
- **Frontend is type-agnostic** — the new app does not read or write `Conversation.Type`. The text WebSocket endpoint and voice WebSocket endpoint are responsible for setting the conversation's type to match their modality on each call.
- **Voice transcripts stream into the message list live** — the assistant's spoken words appear as a streaming markdown message in the conversation in real time, just like text streaming. When the voice session ends, these messages are already persisted.
- **Action button is adaptive** — single button to the right of the input field, content depends on state: mic icon (empty input, idle), send icon (input has text, idle), stop icon (voice session running, red).
- **Voice status strip** — when a voice session is running, a thin colored strip above the input shows a label derived from the hook's `state`:
  - `listening` → `Listening...`
  - `userSpeaking` → `Listening...` (the agent is listening to the user — the strip is the agent's POV, not the user's)
  - `thinking` → `Thinking...`
  - `assistantSpeaking` → `Speaking...`
  - `idle` → strip is hidden
- **Input disabled during voice** — text field is dimmed and non-interactive while voice is running.
- **Conversation row actions** — click to select, hover to reveal a delete button. No editing or model switching in the sidebar.
- **Minimal main view header** — source label (e.g. `app`, `telegram`) and a delete button. No provider/model picker, no stats; that lives in the existing Conversations app.
- **Empty state** — when no conversation is selected, the main view shows `Select a conversation or start a new one`.

## Out of Scope

- Provider/model picker (use the Conversations app)
- Conversation rename / source edit (use the Conversations app)
- Token usage stats (use the Conversations app)
- Tool call inspector (the new app shows tool activity inline like the existing TextApp does, but no expandable details)
- Refactoring or removing the existing `text`, `voice`, or `conversations` apps
- Sharing hooks with the existing TextApp/VoiceApp — this app gets its own copies; if it proves itself we can extract a shared library later

## Backend Changes

### 1. `Message.Modality` field

Add a new column to the `messages` table via the existing `TryAddColumn` migration pattern in `OpenAgent.ConversationStore.Sqlite`.

```csharp
public enum MessageModality
{
    Text,
    Voice
}

public sealed class Message
{
    // ... existing fields ...

    [JsonPropertyName("modality")]
    [JsonConverter(typeof(JsonStringEnumConverter<MessageModality>))]
    public MessageModality Modality { get; init; } = MessageModality.Text;
}
```

- `init`-only, matching the rest of `Message`.
- **JSON wire format:** lowercase string (`"text"` / `"voice"`) via `JsonStringEnumConverter<MessageModality>` — consistent with how `ConversationType` is serialized in `Conversation.cs:21`.
- **SQLite storage:** `INTEGER` column (cast `(int)message.Modality` on insert, cast back on read), matching the pattern used for `Conversation.Type` in `SqliteConversationStore.cs:129`. Migration via `TryAddColumn("Messages", "Modality", "INTEGER NOT NULL DEFAULT 0")`. Default value `0` = `Text`, so existing rows get the right answer for free.
- Channel providers (Telegram, WhatsApp) and scheduled tasks default to `Text` — they can adopt voice modality later if they ever transcribe voice messages.

### 2. Stamp `Modality` on existing message-construction sites

Voice transcripts are already persisted as `Message` rows in `AzureOpenAiVoiceSession.cs:282-292` on `TranscriptDone` (both user and assistant, correct roles). Text user messages are constructed in `WebSocketTextEndpoints.cs:91`, and assistant messages are persisted by the text provider's tool-call loop.

The change in this section is just to stamp `Modality` on every site that constructs a `Message`:

- `AzureOpenAiVoiceSession.cs:285` — set `Modality = MessageModality.Voice` on the AddMessage call.
- `WebSocketTextEndpoints.cs:91` — set `Modality = MessageModality.Text` on the user message.
- `AzureOpenAiTextProvider` and `AnthropicSubscriptionTextProvider` — set `Modality = MessageModality.Text` wherever they persist the assistant message and any tool-call/tool-result rows.
- Channel providers (Telegram, WhatsApp) — leave them unchanged; the `Text` default applies.

No new persistence path. No new logic. Just an init-only field set at the existing construction points.

### 3. Conversation type reconciliation on endpoint hit

Today, `IConversationStore.GetOrCreate` (`SqliteConversationStore.cs:103-108`) early-returns the existing row unchanged if it already exists, which means both `WebSocketTextEndpoints.cs:42` and `WebSocketVoiceEndpoints.cs:39` are no-ops on existing conversations. And `Conversation.Type` is `init`-only (`Conversation.cs:22`), so it can't be mutated as written. Two changes are required:

**3a. Relax `Conversation.Type` to `{ get; set; }`.**

```csharp
[JsonPropertyName("type")]
[JsonConverter(typeof(JsonStringEnumConverter<ConversationType>))]
public required ConversationType Type { get; set; }
```

**3b. Add `IConversationStore.UpdateType(string conversationId, ConversationType type)`.**

A focused, lightweight method — single-row `UPDATE Conversations SET Type = @type WHERE Id = @id`. Implemented in `SqliteConversationStore`. No-op if the row doesn't exist.

The text WebSocket endpoint calls `store.UpdateType(conversationId, ConversationType.Text)` immediately after `GetOrCreate`. The voice WebSocket endpoint calls `store.UpdateType(conversationId, ConversationType.Voice)` likewise. Both calls are idempotent — flipping a Text conversation to Text is a no-op write. Type drives system prompt selection at message-build time, so the next response uses the appropriate prompt. Existing message history is preserved across type changes — the conversation is one continuous thread.

I'd prefer this over folding the side effect into `GetOrCreate`, which would be surprising — the name says "get or create", not "get or create or mutate".

**Cross-app side effect (call out explicitly):** After this change, opening an existing conversation in the **old** TextApp will set `Type = Text`, and opening it in the **old** VoiceApp will set `Type = Voice`. In normal use, users don't cross-hit a single conversation from both old apps (each old app generates its own GUID per session), so the practical impact is nil. But the behavior is technically observable — call it out so nobody is surprised by a type flip in `conversations.db` after the new app ships.

### 4. Sort conversation list by last activity

Requirement says the sidebar is sorted by last activity (newest first). Today `SqliteConversationStore.GetAll()` (`SqliteConversationStore.cs:222`) does `ORDER BY CreatedAt DESC`. Change to:

```sql
ORDER BY COALESCE(LastActivity, CreatedAt) DESC
```

`LastActivity` already exists on `Conversation` (`Conversation.cs:85`) and is populated by message inserts. The `COALESCE` falls back to `CreatedAt` for any conversation that has never had activity recorded.

Backend-side sort, not client-side — keeps the API response order canonical and avoids re-sorting in every consumer.

### 5. No new endpoints

The new app uses existing endpoints exclusively:

- `GET /api/conversations` — sidebar list
- `GET /api/conversations/{id}` — load conversation detail
- `DELETE /api/conversations/{id}` — delete from sidebar
- `WS /ws/conversations/{id}/text` — text streaming
- `WS /ws/conversations/{id}/voice` — voice session

## Frontend Architecture

### File layout

```
src/web/src/apps/chat/
  ChatApp.tsx                  Top-level layout. Owns: selected conversation id, sidebar refresh trigger.
  ChatApp.module.css           Layout grid (sidebar + main split).
  components/
    ConversationSidebar.tsx    List of conversations, + new button, hover-delete on each row.
    ConversationView.tsx       Header + MessageList + Composer for one conversation.
    MessageList.tsx            Renders messages: user bubbles right, assistant markdown left.
    Composer.tsx               Text input + adaptive action button + voice status strip.
  hooks/
    useConversations.ts        Fetch + sort conversation list, expose refresh.
    useConversation.ts         Load one conversation's messages, append on stream events.
    useTextStream.ts           Open text WS, stream CompletionEvents into appended messages.
    useVoiceSession.ts         Open voice WS, mic capture, audio playback, transcript stream.
```

Each file has one responsibility. `ChatApp.tsx` is just layout and the "which conversation is selected" state. The hooks own the messy WebSocket and audio lifecycle so the components stay focused on rendering.

### State ownership

- `ChatApp`: `selectedConversationId: string | null`, `refreshTrigger: number`. When `selectedConversationId` is `null`, renders the empty state in the main pane. Otherwise renders `<ConversationView conversationId={selectedConversationId} />`. The hooks that take a `conversationId` are therefore only mounted when the id is non-null.
- `ConversationSidebar`: uses `useConversations(refreshTrigger)`. Renders rows. Calls `onSelect` and `onNew` props.
- `ConversationView`: receives non-null `conversationId`. Uses `useConversation(conversationId)` for messages, `useTextStream(conversationId)` for text sending, `useVoiceSession(conversationId)` for voice. Renders header, message list, composer.
- `MessageList`: receives `messages` array, renders.
- `Composer`: receives `voiceState: VoiceState | null`, `textStreaming: boolean`, `onSendText`, `onStartVoice`, `onStopVoice`. Owns the input value locally and derives the action button shape from these props plus the local input value.

### Hook contracts

```ts
function useConversations(refreshTrigger: number): {
  conversations: ConversationSummary[];
  loading: boolean;
};

function useConversation(conversationId: string): {
  messages: ConversationMessage[];
  appendMessage: (msg: ConversationMessage) => void;
  updateLastAssistantMessage: (content: string) => void;
};
// 404 from GET /api/conversations/{id} is treated as "empty conversation" — the
// hook returns an empty messages array and clears any error state, instead of
// surfacing the 404 as an error. This handles the new-conversation flow where
// the frontend has generated a GUID but no row exists in the backend yet.
// Any other HTTP error (5xx, network failure) still surfaces as an error.

function useTextStream(conversationId: string): {
  send: (content: string) => void;
  streaming: boolean;
};

function useVoiceSession(conversationId: string): {
  state: 'idle' | 'listening' | 'userSpeaking' | 'thinking' | 'assistantSpeaking';
  start: () => Promise<void>;
  stop: () => void;
  error: string | null;
};
```

The text and voice hooks both need to emit message events that `useConversation` consumes. Implementation: each hook accepts callbacks (`onUserMessage`, `onAssistantDelta`, `onAssistantDone`) so `ConversationView` wires them into `useConversation`'s mutators. This keeps the message store in one place.

### Composer state machine

The composer's action button has four visible states derived from inputs:

| Input value | Voice state | Text streaming | → Action button |
|-------------|-------------|----------------|----------------|
| empty | idle | false | mic icon (start voice) |
| has text | idle | false | send icon (send text) |
| any | running | false | stop icon, red (stop voice) |
| any | idle | true | send icon, disabled (text in flight) |

Text input is disabled when `voiceState !== 'idle'` OR `textStreaming === true`.

### Voice session lifecycle in this app

1. User clicks the mic action button. `useVoiceSession.start()` runs.
2. Microphone permission is requested. AudioContext is created at 24 kHz.
3. WebSocket opens to `/ws/conversations/{conversationId}/voice`.
4. State transitions to `listening`. Composer status strip appears with the current state.
5. User speaks. PCM frames stream out. Assistant audio streams in and plays via the playback queue.
6. As `transcript_delta` events arrive (the voice WS endpoint already emits these per the current `VoiceApp` implementation), the hook forwards them to `useConversation.updateLastAssistantMessage` (for assistant deltas) or appends a user message (on the first user delta of a turn). The result: live character-by-character streaming in the message list, same UX as text streaming.
7. On `transcript_done`, the backend persists the final message (`Modality = Voice`). The frontend's optimistic streaming message is already in place; on the next conversation refresh the persisted row replaces it.
8. User clicks the stop button. `useVoiceSession.stop()` closes the WebSocket, stops the mic, tears down the AudioContext, sets state to `idle`.
9. Status strip disappears. Action button reverts to mic.

### Text send flow in this app

The text WebSocket is long-lived per conversation, matching the existing TextApp pattern: `useTextStream` opens it in a `useEffect` keyed on `conversationId` and closes it on conversation change or unmount. Subsequent sends reuse the open socket.

1. User types into the composer. Action button shows send icon.
2. User clicks send (or presses Enter). `useTextStream.send(content)` runs.
3. User message appended to message list optimistically with `modality=text`.
4. Empty assistant message appended; deltas stream into it.
5. Tool call / result chips render inline as they arrive (same as existing TextApp).
6. On `done` event, streaming flag clears. Composer becomes interactive again.

### New conversation flow

1. User clicks `+` in the sidebar. `ChatApp` generates a new GUID, sets `selectedConversationId` to it. The conversation does not yet exist in the backend.
2. `ConversationView` renders an empty message list with the new id.
3. The first text send or voice start hits the relevant WebSocket endpoint, which creates the conversation via `GetOrCreate`.
4. After the first response, sidebar refresh is triggered so the new conversation appears in the list.

### Registry and icon

- `src/apps/registry.ts`: add a new entry with `id: 'chat'`, `title: 'Chat'`, `icon: 'chat-icon'`, `component: ChatApp`, `defaultSize: { width: 900, height: 600 }`.
- `src/web/public/icons.svg`: `text-icon` (`icons.svg:2`) is already a chat bubble. Add a new `<symbol id="chat-icon">` whose body is `<use href="#text-icon"/>`, or duplicate the path. Either way the visual is the bubble. (If we ever want them visually distinct, this is the place to change it.)

## Visual Reference

See `.superpowers/brainstorm/1107-1775766884/content/layout.html` for the wireframe approved during brainstorming. Key visual decisions:

- Sidebar: ~200px wide, source label on top, truncated id below, hover reveals delete `x`
- Selected row: subtle blue background tint
- Main header: thin row with source label on the left, delete button on the right
- Message list: flex column, gap 12px, padding 14px
- User bubble: right-aligned, blue background, `border-bottom-right-radius: 4px`, max-width 75%
- Assistant message: left-aligned, no background, no border, markdown rendering, max-width 90%
- Voice status strip: thin colored strip above composer, blue tint, dot + label
- Composer: textarea + 38x38 action button, gap 8px, top border separator
- Action button colors: blue for mic/send (idle), red for stop (voice running)

## Error Handling

- **Microphone denied**: voice session error shows under the composer in red, action button reverts to mic. No retry button — user must click mic again.
- **Text WebSocket connection failure**: streaming flag clears, an error toast appears under the message list, the partial assistant message is left in place.
- **Voice WebSocket connection failure**: same — error shown, voice state reverts to idle.
- **Conversation load failure** (clicking a row whose detail GET fails with 5xx or network error): main view shows error state, sidebar refreshes. (404 is not an error — it's treated as an empty conversation per the `useConversation` contract.)
- **Delete conversation failure**: row stays in sidebar, error toast.

No retry logic, no exponential backoff. The user can retry manually.

## Testing

- **Hooks**: unit-test the state machines for `useTextStream` and `useVoiceSession` (mock WebSocket).
- **Composer**: visual test of action button state combinations (input empty/has text × voice idle/running × text streaming on/off).
- **Backend modality persistence**: integration test that sending a message via the text WS results in `modality=text`, sending via voice WS results in `modality=voice`.
- **Backend type reconciliation**: integration test that hitting the text endpoint on a `Type=Voice` conversation flips it to `Type=Text`.
- **No e2e mic test**: voice session integration testing is not feasible in CI without a mic stub. Manual smoke-test only.
