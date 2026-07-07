# Telegram Rich Messages (Bot API 10.1) — Design

- **Date:** 2026-07-07
- **Status:** Draft (pending approval)
- **Tracking issue:** [#62](https://github.com/mbundgaard/OpenAgent/issues/62)
- **Scope owner:** Telegram channel (`OpenAgent.Channel.Telegram`)

## Goal

Make the agent's Telegram replies render as **native rich UI** using Bot API 10.1 "Rich Messages," so the model's existing markdown output (tables, LaTeX, headings, lists, code, quotes) displays faithfully instead of being flattened, and the per-turn "thinking" state renders as a native thinking block.

## Scope

**In (v1):**
- Render the agent's existing **markdown** natively via Rich Messages — no change to what the agent writes.
- Render a native **thinking block** during the per-turn thinking phase, driven by our existing `ThinkingStarted` / `ThinkingStopped` events.
- Raw-HTTP send path for the new methods, behind the existing `ITelegramSender` seam.
- **Zero-regression fallback** to the current HTML rendering path.

**Out (deferred):**
- Teaching the agent an **expressive vocabulary** for rich-only elements with no markdown equivalent (collapsible details, spoiler, pull-quote, sub/superscript, media blocks, maps, anchors). No skill and no system-prompt primer in v1 — the agent learns nothing new.
- Media blocks / collages / slideshows (agent sending images), maps.
- WhatsApp or other channels (no Rich Messages equivalent exists).

**Key consequence of this scope:** v1 is entirely renderer-side. The agent authors exactly as it does today; all work is in our converter + send path. The teaching/skill question is deferred until we add expressive-only elements.

## Background

### Current state
`TelegramMessageHandler` streams **plain-text** draft updates via `ITelegramSender.SendDraftAsync` (raw-HTTP `sendMessageDraft`) as the LLM streams, then sends the **final** message as markdown→Telegram-HTML through `SendHtmlAsync` + `TelegramMarkdownConverter.ToTelegramHtml`, chunked by `ChunkMarkdown`.

`TelegramMarkdownConverter` is a hand-rolled, lossy markdown→HTML downconverter:
- Headings → bold (all levels collapse) — `:128`
- Lists / task lists → plain indented text — `:199`
- Dividers → literal `---` — `:187`
- Tables → **not handled** (pipeline only enables strikethrough — `:17`)
- LaTeX / math → dropped
- No collapsible details, spoiler, sub/superscript, media, pull-quotes

### Feasibility finding
- **`Telegram.Bot`** (our library), latest **22.9.6**, only covers Bot API **9.6** — no Rich Messages.
- **`Telegram.BotAPI` 10.1.0** — a *separate, different* .NET library — has full Rich Messages support.
- Our channel **already hand-rolls raw HTTP** for methods missing from `Telegram.Bot` (`SendDraftAsync` → `sendMessageDraft`), behind `ITelegramSender`. Rich Messages slots into this exact pattern.

### Rich Messages API surface (from Bot API 10.1 changelog — TO BE VERIFIED against the live reference before coding)
- Methods: `sendRichMessage`, `sendRichMessageDraft`, `editMessageText` gains `rich_message`.
- `Message` gains a `rich_message` field.
- Block types include: `RichBlockParagraph`, `RichBlockSectionHeading`, `RichBlockPreformatted`, `RichBlockDivider`, `RichBlockMathematicalExpression`, `RichBlockList`, `RichBlockBlockQuotation`, `RichBlockTable` / `RichBlockTableCell`, `RichBlockThinking`, plus media/expressive blocks we are not using in v1.

## Approach decision

**Chosen: raw-HTTP for the new methods, behind `ITelegramSender`. Keep `Telegram.Bot` for everything else.**

Alternatives considered:
1. **Raw HTTP for the 3 new methods (chosen).** Additive, low-risk, matches the established `sendMessageDraft` pattern. No churn to receiving / standard sends / webhook. We own a small set of DTOs.
2. **Migrate to `Telegram.BotAPI` 10.1.0.** Full typed coverage of 10.1, but a large rewrite of the whole channel (different API surface: `TelegramMessageHandler`, `TelegramBotClientSender`, webhook endpoints, receiving) for uncertain benefit. Rejected for v1.
3. **Wait for `Telegram.Bot` to ship 10.1.** Unknown timeline (they're at 9.6). Rejected — blocks the work indefinitely.

## Architecture

### Components

1. **Rich block model** — `OpenAgent.Channel.Telegram/RichMessages/` — hand-rolled DTOs for the subset we emit (paragraph, section heading, list, table + cell, preformatted/code, block-quotation, divider, mathematical expression, thinking) plus the inline `RichText` run model (bold, italic, underline, strikethrough, code, math, link). `[JsonPropertyName]` on every field; serialized to the Rich Messages JSON shape.

2. **Markdown → RichBlock converter** — replace `TelegramMarkdownConverter.ToTelegramHtml` with a `ToRichMessage(string markdown) : RichMessage` that walks the **same Markdig AST** we already walk, emitting `RichBlock`s instead of HTML strings. Enable Markdig's pipe-table extension. Inline emphasis maps to `RichText` styling instead of HTML tags. Same safe-link handling (http/https only).

3. **`ITelegramSender` additions** — new methods:
   - `SendRichMessageAsync(chatId, RichMessage, ct) : int` → `sendRichMessage`
   - `SendRichDraftAsync(chatId, draftId, RichMessage, ct) : DraftResult` → `sendRichMessageDraft`
   Implemented in `TelegramBotClientSender` via the existing private `HttpClient`, mirroring `SendDraftAsync`'s error/`retry_after` handling.

4. **`TelegramMessageHandler` streaming loop** — updated to:
   - On `ThinkingStarted`: draft a **thinking block** via `SendRichDraftAsync`.
   - During answer streaming: keep drafts **lightweight** (see Streaming strategy).
   - On completion: send the **final** message via `SendRichMessageAsync` with the full markdown→RichBlock conversion.

### Data flow (per turn)

```
user msg → TelegramMessageHandler
  ThinkingStarted  → SendRichDraftAsync(thinking block)      [native "thinking…"]
  ...tool rounds...
  ThinkingStopped
  text deltas      → lightweight draft updates                [no flicker]
  turn complete    → ToRichMessage(fullReply)
                   → SendRichMessageAsync(rich blocks)         [tables/LaTeX/headings native]
  on any rich error→ fallback: SendHtmlAsync(ToTelegramHtml)   [current path]
```

## Streaming strategy (resolves the "partial markdown" risk)

Mid-stream, markdown is incomplete (half-open table, unclosed code fence). Re-parsing partial markdown to rich blocks every draft tick would flicker broken structure. Known third-party reports confirm naive edit-streaming destroys rich formatting.

**v1 decision:**
- **Thinking phase** → native rich **thinking block** in the draft.
- **Answer phase** → stream **lightweight** drafts (plain paragraph text, as today) while tokens arrive.
- **Final message** → full markdown→RichBlock conversion via `sendRichMessage`.

This delivers both objectives (native thinking block + fully rich final answer) while keeping streaming robust. Progressive *rich* answer streaming (block-boundary flushing) is a later refinement, not v1.

## Thinking block content

Our `ThinkingStarted` / `ThinkingStopped` are per-turn **brackets** around tool execution with no reasoning-text payload. So the thinking block is a **state affordance** ("thinking…"), not streamed reasoning.

**v1 decision:** plain thinking state. Do **not** surface internal tool names (avoids leaking tool internals to end users). Surfacing tool activity is a later option.

## Error handling & fallback

- Every rich send is wrapped: on non-success (feature unavailable, malformed payload, `retry_after`), fall back to the existing `SendHtmlAsync(ToTelegramHtml(...))` + `ChunkMarkdown` path for that message. Existing `DraftResult` retry/`retry_after` handling is reused.
- A single feature-detection failure downgrades the turn to HTML rather than erroring the reply. The current path is retained in full as the fallback — nothing is deleted in v1.

## Testing

- **Converter unit tests** — markdown fixtures → expected `RichMessage` block trees: headings, nested lists, pipe tables, fenced code (with language), block quotes, inline emphasis combinations, math, links (safe/unsafe schemes), and the existing chunking-size behavior. Mirror the style of existing converter tests.
- **Sender tests** — `ITelegramSender` fake asserts `SendRichMessageAsync` / `SendRichDraftAsync` are called with the expected block payloads; a rich-send failure triggers the HTML fallback.
- **Handler tests** — extend `TelegramMessageHandlerTests`: `ThinkingStarted` produces a thinking-block draft; final send uses rich; forced rich failure falls back to HTML. Reply-quoting and mention-filter paths still pass unchanged.

## Risks / open questions

1. **API schema not yet verified.** Method/field/block names above come from the changelog + secondary sources, not the live Bot API reference. **First implementation step is a spike:** read the live `sendRichMessage` / `sendRichMessageDraft` / `RichBlock*` reference and pin the exact JSON schema (esp. how `sendRichMessageDraft` correlates to a message vs our `draftId` concept, and the inline text-run shape).
2. **Draft correlation.** Confirm whether `sendRichMessageDraft` uses the same `draft_id` mechanic as `sendMessageDraft` or a different handle.
3. **Rich + fallback parity.** Ensure the fallback HTML path produces the same chunking behavior for over-length messages (Rich Messages size limits TBD).
4. **`Telegram.Bot` catch-up.** If the library later ships 10.1, we can migrate the raw-HTTP methods onto it; the `ITelegramSender` seam isolates that change.

## Prerequisite before implementation

Spike task: verify the exact Rich Messages JSON schema against the live Bot API reference and confirm the draft-streaming correlation model. Everything else depends on it.

## Out of scope (explicitly deferred)

- Expressive-only vocabulary + the teaching mechanism (skill vs system-prompt primer) — revisit when adding collapsible/spoiler/media/maps.
- Agent-authored media (images/charts), maps, slideshows.
- WhatsApp / other channels.
