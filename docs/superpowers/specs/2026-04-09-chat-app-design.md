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
- **Voice status strip** — when a voice session is running, a thin colored strip above the input shows `Listening...` / `Speaking...` / `Thinking...`. Disappears when idle.
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
    Text,   // Default for existing rows and any message that doesn't specify
    Voice
}

public sealed class Message
{
    // ... existing fields ...

    [JsonPropertyName("modality")]
    public MessageModality Modality { get; set; } = MessageModality.Text;
}
```

- Stored as `TEXT` column with values `"text"` or `"voice"` (lowercase, for forward-compat with future modalities).
- Existing rows get the default `text` on migration.
- Set by whichever endpoint produces the message:
  - Text WebSocket endpoint sets `modality = Text` on user and assistant messages it persists
  - Voice WebSocket endpoint sets `modality = Voice` on user and assistant transcripts it persists
- Channel providers (Telegram, WhatsApp) continue to default to `Text` — they can adopt voice modality later if they ever transcribe voice messages.

### 2. Voice transcript persistence as Messages

Verify that the voice WebSocket endpoint already persists user and assistant transcripts as `Message` rows on the conversation. If it does not, add this:

- On `transcript_done` events from the voice provider, write a `Message` to the conversation store with `modality = Voice` and the appropriate role.
- This is the same persistence path the text WebSocket uses; voice just sets a different modality.

If verification reveals voice transcripts are already persisted, the only change in this section is the modality field assignment.

### 3. Conversation type reconciliation on endpoint hit

The new chat app does not pass conversation type when creating a conversation. The text and voice WebSocket endpoints must set the type implicitly:

- Text WebSocket endpoint: on first message to a new conversation, create with `Type = Text`. On a subsequent message to a conversation that exists with `Type = Voice`, update it to `Type = Text` before processing.
- Voice WebSocket endpoint: same logic for `Type = Voice`.

Type drives system prompt selection at message-build time, so flipping the type between calls means the next response uses the appropriate prompt. Existing message history is preserved across type changes — the conversation is one continuous thread.

This is a behavior change for the existing TextApp/VoiceApp too, but it's backwards compatible: a conversation that's only ever used by one transport never sees a type change.

### 4. No new endpoints

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
6. As `transcript_delta` events arrive, the user/assistant transcript is buffered in the hook.
7. As `transcript_delta` events arrive (the existing voice WS endpoint already emits these per the current VoiceApp implementation), the assistant message in the list streams character-by-character — same UX as text streaming.
8. On `transcript_done`, the message is committed (modality=voice) by the backend.
9. User clicks the stop button. `useVoiceSession.stop()` closes the WebSocket, stops the mic, tears down the AudioContext, sets state to `idle`.
10. Status strip disappears. Action button reverts to mic.

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
- `src/web/public/icons.svg`: add a new `<symbol id="chat-icon">` (a chat bubble — re-using the design that was just removed when `chat-icon` was renamed to `text-icon` is fine).

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
- **Conversation load failure** (clicking a row that 404s): main view shows error state, sidebar refreshes.
- **Delete conversation failure**: row stays in sidebar, error toast.

No retry logic, no exponential backoff. The user can retry manually.

## Testing

- **Hooks**: unit-test the state machines for `useTextStream` and `useVoiceSession` (mock WebSocket).
- **Composer**: visual test of action button state combinations (input empty/has text × voice idle/running × text streaming on/off).
- **Backend modality persistence**: integration test that sending a message via the text WS results in `modality=text`, sending via voice WS results in `modality=voice`.
- **Backend type reconciliation**: integration test that hitting the text endpoint on a `Type=Voice` conversation flips it to `Type=Text`.
- **No e2e mic test**: voice session integration testing is not feasible in CI without a mic stub. Manual smoke-test only.
