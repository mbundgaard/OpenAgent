# TelegramMessageHandler Refactor Plan

**Date:** 2026-03-14
**File:** `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`
**Context:** Handler has grown with streaming, thinking output, reply-to, and channel message ID features. Code review identified 5 concerns.

## 1. Extract shared send+update logic

**Problem:** The "chunk, send, update channel ID" block is duplicated between `StreamResponseAsync` (lines 286-303) and `CollectAndSendAsync` (lines 341-358).

**Fix:** Extract into a shared method like `SendChunkedResponseAsync(sender, chatId, replyText, assistantMessageId, ct)`.

**Status:** [x] Done — extracted `SendFinalResponseAsync`

## 2. Break up StreamResponseAsync

**Problem:** 180 lines doing buffer management, draft consumer task, thinking output, tool line collection, error handling, chunking, and channel ID update.

**Fix:** After #1 removes the tail, extract thinking/tool-line logic (the `ToolCallEvent`/`ToolResultEvent` handling + blockquote send) into a helper. Keep the producer loop focused on dispatching events.

**Status:** [x] Done — extracted `RunDraftConsumerAsync`, `SendThinkingMessageAsync`, `FormatToolLine`

## 3. Consumer task exception handling

**Problem:** If `SendDraftAsync` throws an unexpected exception (not a failed `DraftResult`), the consumer task dies silently. Only `DraftResult` failures with status codes are handled.

**Fix:** Wrap the `SendDraftAsync` call in a try/catch inside the consumer loop. Log the exception and continue (or break with error state).

**Status:** [ ] Not started

## 4. Multi-round thinking output

**Problem:** `thinkingSent = true` on line 245 prevents subsequent tool call rounds from showing their tool lines. If the LLM does 3 rounds of tools, only the first round's thinking is displayed.

**Decision needed:** Should subsequent rounds show additional thinking messages?

**Recommendation:** Yes — send a new blockquote for each tool call round. Reset `toolLines` and `thinkingSent` after sending, or accumulate per-round.

**Status:** [ ] Not started — awaiting decision

## 5. Chunked message IDs

**Problem:** When a long response splits into multiple Telegram messages (chunks), only the last chunk's Telegram message ID is stored. If the user replies to an earlier chunk, the reference won't match.

**Options:**
- **(a) Store first chunk's ID** — most likely to be replied to, simplest change
- **(b) Store all chunk IDs** — needs one-to-many model (e.g. comma-separated or separate table)
- **(c) Store last chunk's ID** — current behavior, least useful

**Decision needed:** Which option?

**Recommendation:** (a) Store first chunk's ID — simple and covers the most common case.

**Status:** [ ] Not started — awaiting decision
