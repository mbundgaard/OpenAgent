# Telegram Rich Messages (Bot API 10.1) — Design

- **Date:** 2026-07-07 (revised after schema spike)
- **Status:** Approved — simplified approach
- **Tracking issue:** [#62](https://github.com/mbundgaard/OpenAgent/issues/62)
- **Scope owner:** Telegram channel (`OpenAgent.Channel.Telegram`)

## Goal

Make the agent's Telegram replies render as **native rich UI** (tables, LaTeX, headings, lists, code, quotes) using Bot API 10.1 Rich Messages, instead of being flattened by our lossy markdown→HTML converter.

## Spike findings (authoritative — from `Eptagone/Telegram.BotAPI` 10.1.0 source)

The spike changed the approach. **What a bot *sends* is text, not a block tree:**

```csharp
class InputRichMessage {            // the `rich_message` payload
    string? Html;                   // "html"
    string? Markdown;               // "markdown"
    bool?   IsRtl;                  // "is_rtl"
    bool?   SkipEntityDetection;    // "skip_entity_detection"
}
```

- `sendRichMessage(chat_id, rich_message: InputRichMessage, …)`
- `sendRichMessageDraft(chat_id, draft_id: int, rich_message: InputRichMessage)` — `draft_id` is the same correlation mechanic as our existing `sendMessageDraft`.
- `editMessageText` gains a `rich_message` parameter.
- The `RichMessage` / `RichBlock` / `RichText` typed trees are the **received** representation only (`Message.rich_message`); bots do **not** author them.

**Consequences:**
- We send `rich_message = { markdown: <agent markdown> }`. Telegram parses it server-side into rich blocks.
- **No DTO model and no markdown→RichBlock converter are needed** (both were in the earlier draft of this design; dropped).
- **The thinking block is deferred.** `InputRichMessage` has no `blocks` field and no known markdown/html syntax for `RichBlockThinking` — it appears to be a received/system-rendered type a bot cannot author. Out of scope for v1.

## Scope

**In (v1):**
- Send the agent's existing markdown as a Rich Message (`rich_message.markdown`) for the **final** reply and for **streaming drafts**.
- **Zero-regression fallback** to the current HTML path (`ToTelegramHtml` + `ChunkMarkdown`) on any rich-send failure.
- A `RichMessages` kill-switch on the connection config.

**Out (deferred):**
- The native **thinking block** (not authorable via `InputRichMessage`).
- Any expressive-only vocabulary + teaching (skill / system-prompt primer). The agent authors nothing new.
- Parsing received `rich_message` trees; media/maps; WhatsApp and other channels.

## Open item resolved by a live smoke test (first implementation step)

The docs page truncated before the `InputRichMessage` detail, so the exact **markdown dialect** the field accepts is unconfirmed: does the agent's existing markdown render tables/LaTeX/headings as-is, or does it need light adaptation (escaping, fenced-block syntax)? This is answered empirically by sending a sample to a test chat and observing — **not** by more doc-scraping. `InputRichMessage.html` (which we can already produce via `ToTelegramHtml`) is an intermediate option if the markdown dialect proves finicky, but `markdown` is the target because it carries the rich elements our HTML drops.

## Approach decision

**Chosen: raw-HTTP `sendRichMessage` / `sendRichMessageDraft` behind `ITelegramSender`, sending `{ markdown }`. Keep `Telegram.Bot` for everything else.**

- `Telegram.Bot` 22.9.6 only covers Bot API 9.6, so these methods stay hand-rolled (the exact pattern already used for `sendMessageDraft`).
- Migrating the whole channel to `Telegram.BotAPI` 10.1.0 is rejected for v1 — unnecessary given we only need two methods and a text payload.

## Architecture

### Components

1. **`ITelegramSender` additions:**
   - `SendRichMarkdownAsync(chatId, markdown, ct) : int` → `sendRichMessage` with `{ chat_id, rich_message: { markdown } }`, returns `message_id`.
   - `SendRichMarkdownDraftAsync(chatId, draftId, markdown, ct) : DraftResult` → `sendRichMessageDraft` with `{ chat_id, draft_id, rich_message: { markdown } }`.
   Implemented in `TelegramBotClientSender` via the existing private `HttpClient`, mirroring `SendDraftAsync`'s error/`retry_after` handling.

2. **`TelegramMessageHandler`:**
   - **Final reply** (`SendFinalResponseAsync` :520): when the flag is on, send the full `replyText` as rich markdown; on failure fall back to the existing chunk + `ToTelegramHtml` + `SendWithRetryAsync` path.
   - **Streaming drafts** (`RunDraftConsumerAsync` :347): send the accumulated partial markdown via `SendRichMarkdownDraftAsync` each tick instead of the plain draft; on rich-draft failure, fall back to the existing plain `SendDraftAsync`. Telegram re-parses each draft, so partial markdown is handled server-side (transient, self-correcting as more arrives).

3. **`TelegramOptions.RichMessages`** (bool, default true) — kill-switch parsed from connection config; the handler checks it before attempting any rich send.

### Data flow (per turn)

```
user msg → TelegramMessageHandler
  streaming    → SendRichMarkdownDraftAsync(partial markdown)   [rich, self-correcting]
                 (on failure → plain SendDraftAsync)
  turn complete→ SendRichMarkdownAsync(full markdown)           [tables/LaTeX/headings native]
                 (on failure → ToTelegramHtml + SendWithRetryAsync)  [current path]
```

## Error handling & fallback

- Every rich send is wrapped; on non-success (feature unavailable, malformed payload, `retry_after`) it falls back to the existing HTML/plain path for that message/draft. The current path is retained in full — nothing is deleted.
- The `[]` suppression sentinel and the empty→"OK!" guard in `SendFinalResponseAsync` are preserved.

## Testing

- **Sender tests** — `SendRichMarkdownAsync` / `SendRichMarkdownDraftAsync` POST the expected `rich_message.markdown` body and parse `message_id` / `retry_after`; non-200 yields `DraftResult.Ok == false`. Use an injected `HttpMessageHandler` (test constructor).
- **Handler tests** — extend `TelegramMessageHandlerTests`: flag on → final send uses rich markdown; forced rich failure → falls back to HTML; flag off → HTML path directly; streaming uses rich draft, falls back to plain on failure; `[]` and empty guards still hold; reply-quoting / mention-filter paths unchanged.
- **Live smoke test** (manual, first step) — send table / fenced code / heading+list / LaTeX via `sendRichMessage` to a test chat; confirm native rendering and record any markdown-dialect adaptation needed.

## Risks / open questions

1. **Markdown dialect** — resolved by the smoke test; may require light adaptation of the agent's markdown or use of the `html` field as an intermediate.
2. **Streaming re-parse cost / flicker** — partial markdown re-parsed each draft tick; expected acceptable and self-correcting, but confirm in the smoke test; if bad, stream plain and only the final is rich.
3. **`Telegram.Bot` catch-up** — if it later ships 10.1, migrate the raw-HTTP methods onto it; the `ITelegramSender` seam isolates that.

## Out of scope (explicitly deferred)

- Native thinking block, expressive-only vocabulary + teaching mechanism, received-tree parsing, media/maps, WhatsApp/other channels.
