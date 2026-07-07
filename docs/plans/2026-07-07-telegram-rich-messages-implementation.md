# Telegram Rich Messages Implementation Plan (revised — simplified)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Render the agent's existing markdown (tables, LaTeX, headings, lists, code, quotes) as native Telegram Bot API 10.1 Rich Messages by sending `rich_message = { markdown }`, with a zero-regression fallback to the current HTML path.

**Architecture:** No block-tree DTOs and no markdown→block converter — the spike confirmed a bot *sends* text (`InputRichMessage = { html?, markdown?, is_rtl?, skip_entity_detection? }`) and Telegram parses it server-side. We add two raw-HTTP send methods (`sendRichMessage`, `sendRichMessageDraft`) behind the existing `ITelegramSender` seam, wire them into the handler's final-send and streaming-draft paths, and fall back to the current `ToTelegramHtml` path on failure. Thinking block deferred (not authorable via `InputRichMessage`).

**Tech Stack:** .NET 10, System.Text.Json, xUnit, `Telegram.Bot` 22.9.6 (retained; the two new methods are hand-rolled HTTP, same pattern as the existing `sendMessageDraft`).

**Design doc:** `docs/plans/2026-07-07-telegram-rich-messages-design.md` · **Issue:** #62

**Verified schema (from `Eptagone/Telegram.BotAPI` 10.1.0 source):**
- `sendRichMessage` body: `{ chat_id, rich_message: { markdown?: string, html?: string, is_rtl?: bool, skip_entity_detection?: bool }, ... }` → returns a `Message` (use `result.message_id`).
- `sendRichMessageDraft` body: `{ chat_id, draft_id: int, rich_message: { markdown?: string, ... } }`.

---

## Task 1: Live smoke test — confirm the markdown dialect (BLOCKING, non-TDD)

Confirms whether the agent's existing markdown renders natively as-is, or needs light adaptation. Needs a Telegram **bot token** and a **chat_id** to send to (ask the user for a throwaway test bot, or a chat where the existing bot may post).

**Steps:**
1. With a test bot token `T` and chat `C`, send via curl:
   ```bash
   curl -s "https://api.telegram.org/bot$T/sendRichMessage" -H "Content-Type: application/json" \
     -d '{"chat_id":C,"rich_message":{"markdown":"# Heading\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n- item one\n- item two\n\n```py\nx=1\n```\n\nInline $E=mc^2$ and:\n$$\\int_0^1 x\\,dx$$\n\n> a quote"}}'
   ```
2. Observe the rendered message in Telegram. Record: does the table render? LaTeX (inline + block)? heading? list? fenced code? Note the exact `ok`/`description` from the API response for any rejects.
3. If the dialect differs from CommonMark (escaping, table syntax, math delimiters), record the exact adaptation needed. If markdown is rejected outright, retry with `{"html": "<b>…</b>…"}` to confirm the `html` field works as the fallback-rich path.
4. Append findings to the design doc under "Smoke-test results" and note any adaptation the converter-free send path must apply (ideally none).

**Commit:**
```bash
git add docs/plans/2026-07-07-telegram-rich-messages-design.md
git commit -m "docs: record Rich Messages markdown dialect smoke-test results"
```

> If markdown needs non-trivial transformation, STOP and report — that would reintroduce converter work and should be re-scoped, not silently built.

---

## Task 2: `ITelegramSender` — rich markdown send methods (raw HTTP)

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/ITelegramSender.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramBotClientSender.cs`
- Test: `src/agent/OpenAgent.Tests/Telegram/TelegramRichSenderTests.cs` (create)

**Step 1: Add to `ITelegramSender`:**
```csharp
/// <summary>Sends the given markdown as a Bot API 10.1 Rich Message. Returns the Telegram message ID.</summary>
Task<int> SendRichMarkdownAsync(ChatId chatId, string markdown, CancellationToken ct);

/// <summary>Streams a partial Rich Message draft from markdown (Bot API 10.1).</summary>
Task<DraftResult> SendRichMarkdownDraftAsync(ChatId chatId, long draftId, string markdown, CancellationToken ct);
```

**Step 2: Write the failing test** — inject a stub `HttpMessageHandler` into `TelegramBotClientSender` (add an `internal` test constructor accepting an `HttpClient`). Assert:
- `SendRichMarkdownAsync` POSTs to `sendRichMessage` with body containing `"rich_message":{"markdown":"…"}` and returns the parsed `result.message_id`.
- On a non-200 with `{"parameters":{"retry_after":3}}`, `SendRichMarkdownDraftAsync` returns `DraftResult.Ok == false` with `RetryAfterSeconds == 3`.

```csharp
// sketch
var handler = new StubHandler(/* returns {"ok":true,"result":{"message_id":42}} */);
var sender = new TelegramBotClientSender(new HttpClient(handler){ BaseAddress = new Uri("https://api.telegram.org/botTEST/") });
var id = await sender.SendRichMarkdownAsync(123L, "# hi", CancellationToken.None);
Assert.Equal(42, id);
Assert.Contains("\"markdown\":\"# hi\"", handler.LastBody);
Assert.Contains("sendRichMessage", handler.LastPath);
```

**Step 3: Run — expect FAIL.**
Run: `dotnet test --filter "FullyQualifiedName~TelegramRichSenderTests"`

**Step 4: Implement** in `TelegramBotClientSender`, mirroring `SendDraftAsync`:
```csharp
public async Task<int> SendRichMarkdownAsync(ChatId chatId, string markdown, CancellationToken ct)
{
    var payload = new { chat_id = chatId.Identifier ?? (object)chatId.Username!, rich_message = new { markdown } };
    var response = await _httpClient.PostAsJsonAsync("sendRichMessage", payload, ct);
    response.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    return doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt32();
}

public async Task<DraftResult> SendRichMarkdownDraftAsync(ChatId chatId, long draftId, string markdown, CancellationToken ct)
{
    var payload = new { chat_id = chatId.Identifier, draft_id = draftId, rich_message = new { markdown } };
    var response = await _httpClient.PostAsJsonAsync("sendRichMessageDraft", payload, ct);
    if (response.IsSuccessStatusCode) return DraftResult.Success();
    // reuse the exact error-body/retry_after parsing block from SendDraftAsync
    return await ParseDraftFailureAsync(response, ct);
}
```
Extract the failure-parsing block from `SendDraftAsync` into a shared private `ParseDraftFailureAsync` to keep it DRY.

**Step 5: Run — expect PASS. Commit.**
```bash
git add -A && git commit -m "feat(telegram): raw-HTTP sendRichMessage/sendRichMessageDraft (markdown)"
```

---

## Task 3: `RichMessages` kill-switch on `TelegramOptions`

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramOptions.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProviderFactory.cs` (parse from connection `Config`, default true)
- Test: extend the factory tests if present.

Add `public bool RichMessages { get; init; } = true;` and parse it from the connection config (default true when absent). Single bool — YAGNI.

**Commit:**
```bash
git add -A && git commit -m "feat(telegram): add RichMessages kill-switch to TelegramOptions"
```

---

## Task 4: Handler — final reply as rich markdown, HTML fallback

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs` (`SendFinalResponseAsync` :520, `SendWithRetryAsync` :562)
- Test: `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`

**Step 1: Failing tests** with a fake `ITelegramSender`:
- (a) flag on → `SendRichMarkdownAsync` is called with the full (un-chunked) `replyText`.
- (b) `SendRichMarkdownAsync` throws → falls back to `SendHtmlAsync` (existing path).
- (c) flag off → HTML path directly, `SendRichMarkdownAsync` never called.
- (d) `[]` suppression and empty→"OK!" guards still hold.

**Step 2: Run — expect FAIL.**

**Step 3: Implement** — in `SendFinalResponseAsync`, after the `[]`/empty guards: if `_options.RichMessages`, call a new `SendRichWithFallbackAsync(sender, chatId, replyText, ct)` that tries `SendRichMarkdownAsync(chatId, replyText, ct)` and on exception falls back to the existing chunk+`ToTelegramHtml`+`SendWithRetryAsync` loop. Capture the returned message id for reply-to tracking (`UpdateChannelMessageId`). When the flag is off, keep the current behavior exactly.

**Step 4: Run — expect PASS.** Then `dotnet test --filter "FullyQualifiedName~Telegram"`.

**Step 5: Commit.**
```bash
git add -A && git commit -m "feat(telegram): send final reply as Rich Message with HTML fallback"
```

---

## Task 5: Handler — streaming drafts as rich markdown, plain fallback

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs` (`RunDraftConsumerAsync` :347)
- Test: `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`

**Step 1: Failing test** — during streaming with the flag on, the draft consumer calls `SendRichMarkdownDraftAsync` with the accumulated snapshot; if it returns `!Ok`/throws, the consumer falls back to the plain `SendDraftAsync` for subsequent ticks (and honors the existing `retry_after` backoff).

**Step 2: Run — expect FAIL.**

**Step 3: Implement** — in `RunDraftConsumerAsync`, when `_options.RichMessages`, replace the `SendDraftAsync(chatId, draftId, snapshot, null, ct)` call with `SendRichMarkdownDraftAsync(chatId, draftId, snapshot, ct)`. Keep the identical `DraftResult` handling (backoff on `!Ok`, `lastSentLength` tracking). On a thrown exception or repeated `!Ok`, degrade to plain `SendDraftAsync` (a simple `bool useRich` that flips false after a rich failure). Flag off → unchanged plain path.

**Step 4: Run — expect PASS.**

**Step 5: Commit.**
```bash
git add -A && git commit -m "feat(telegram): stream drafts as Rich Messages with plain fallback"
```

---

## Task 6: Full regression + manual verification

**Steps:**
1. `dotnet test` (full). Green except the known parallel `WebApplicationFactory` flake (re-run any endpoint failures in isolation — see CLAUDE.md). New Telegram tests must pass deterministically.
2. Manual: a real test bot connection with `RichMessages=true` — prompt the agent to produce a table, fenced code, heading+list, and (if the smoke test confirmed it) LaTeX; confirm native rendering and smooth streaming. Force a rich failure (temporarily point the base URL wrong) and confirm clean HTML fallback. Use `@ superpowers:verification-before-completion` before claiming done.
3. Update the design doc with final results; note LaTeX/math status if the dialect didn't support it.

**Commit:**
```bash
git add -A && git commit -m "docs: finalize Rich Messages results and any deferred items"
```

---

## Out of scope (do NOT implement here)

- Native thinking block (not authorable via `InputRichMessage`).
- Expressive-only vocabulary + teaching (skill / system-prompt primer).
- Parsing received `rich_message` trees; media/maps; WhatsApp / other channels.

## Notes for the executor

- Keep `ToTelegramHtml` and `ChunkMarkdown` intact — they are the fallback.
- The agent authors nothing new; do not touch system prompt / skills / providers.
- If Task 1's smoke test shows the markdown dialect needs non-trivial transformation, STOP and report — do not silently reintroduce converter work.
- Follow existing test style in `TelegramMessageHandlerTests`.
