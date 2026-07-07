# Telegram Rich Messages Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Render the agent's existing markdown (tables, LaTeX, headings, lists, code, quotes) and a per-turn thinking state as native Telegram Bot API 10.1 Rich Messages, with a zero-regression fallback to the current HTML path.

**Architecture:** A hand-rolled `RichMessage`/`RichBlock` DTO model serializes to the Rich Messages JSON. `TelegramMarkdownConverter` gains a `ToRichMessage` walk over the same Markdig AST it already uses. New `sendRichMessage` / `sendRichMessageDraft` calls go out as raw HTTP behind the existing `ITelegramSender` seam (same pattern as the current hand-rolled `sendMessageDraft`). `TelegramMessageHandler` sends a rich thinking-state draft during thinking, streams lightweight during the answer, and sends the final answer as a rich message — falling back to HTML on any rich-send failure. No library migration; the agent authors nothing new.

**Tech Stack:** .NET 10, System.Text.Json (polymorphic serialization), Markdig, xUnit, `Telegram.Bot` 22.9.6 (retained for everything except the three new methods).

**Design doc:** `docs/plans/2026-07-07-telegram-rich-messages-design.md` · **Issue:** #62

---

## Task 1: Spike — pin the Rich Messages JSON schema (BLOCKING, non-TDD)

Everything downstream depends on the exact wire schema, which is not yet verified. Do this first and do not write model code until it's done.

**Files:**
- Modify: `docs/plans/2026-07-07-telegram-rich-messages-design.md` (append a "Verified API schema" appendix)

**Steps:**
1. Read the live Bot API reference for: `sendRichMessage`, `sendRichMessageDraft`, `editMessageText` (`rich_message` param), the `RichMessage` container, and the block types we emit: paragraph, section heading, preformatted, list, block-quotation, divider, mathematical expression, table (+ cell), thinking. Also the inline text-run / `RichText` shape.
2. Record, exactly: the JSON property names, the block **type discriminator** (property name + value strings), how inline styled runs are represented, and how `sendRichMessageDraft` correlates to a message (does it reuse a `draft_id` like `sendMessageDraft`, or a different handle?).
3. Note any size limits for a rich message (affects chunking/fallback).
4. Append all of the above to the design doc under "Verified API schema" and reconcile any names that differ from the changelog-derived guesses used in this plan. **If a name in a later task differs from what you verified, the verified name wins** — update the code accordingly.

**Commit:**
```bash
git add docs/plans/2026-07-07-telegram-rich-messages-design.md
git commit -m "docs: pin verified Telegram Rich Messages JSON schema"
```

---

## Task 2: RichBlock / RichText DTO model

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/RichMessages/RichModels.cs`
- Test: `src/agent/OpenAgent.Tests/Telegram/RichModelsSerializationTests.cs`

> Reconcile every `[JsonPropertyName]` and discriminator value below with Task 1's verified schema before finalizing.

**Step 1: Write the failing serialization test**

```csharp
using System.Text.Json;
using OpenAgent.Channel.Telegram.RichMessages;

namespace OpenAgent.Tests.Telegram;

public class RichModelsSerializationTests
{
    private static readonly JsonSerializerOptions Opts = RichMessage.JsonOptions;

    [Fact]
    public void Paragraph_with_bold_run_serializes_to_expected_shape()
    {
        var msg = new RichMessage
        {
            Blocks =
            [
                new RichParagraphBlock
                {
                    Text = [ new RichTextRun { Text = "hi" }, new RichTextRun { Text = "bold", Bold = true } ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(msg, Opts);

        Assert.Contains("\"type\":\"paragraph\"", json);
        Assert.Contains("\"bold\":true", json);
        Assert.DoesNotContain("\"bold\":false", json); // nulls/defaults omitted
    }
}
```

**Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RichModelsSerializationTests" `
Expected: FAIL — types don't exist.

**Step 3: Implement the model**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Channel.Telegram.RichMessages;

/// <summary>Top-level Rich Message payload (Bot API 10.1). Serializes to the `rich_message` object.</summary>
public sealed class RichMessage
{
    [JsonPropertyName("blocks")]
    public required IReadOnlyList<RichBlock> Blocks { get; init; }

    /// <summary>Shared options: camelCase-free explicit names, omit nulls/defaults.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

// Polymorphic block hierarchy. Discriminator property/value strings MUST match Task 1's schema.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RichParagraphBlock), "paragraph")]
[JsonDerivedType(typeof(RichHeadingBlock), "section_heading")]
[JsonDerivedType(typeof(RichListBlock), "list")]
[JsonDerivedType(typeof(RichTableBlock), "table")]
[JsonDerivedType(typeof(RichCodeBlock), "preformatted")]
[JsonDerivedType(typeof(RichQuoteBlock), "block_quotation")]
[JsonDerivedType(typeof(RichDividerBlock), "divider")]
[JsonDerivedType(typeof(RichMathBlock), "mathematical_expression")]
[JsonDerivedType(typeof(RichThinkingBlock), "thinking")]
public abstract class RichBlock { }

public sealed class RichParagraphBlock : RichBlock
{
    [JsonPropertyName("text")] public required IReadOnlyList<RichTextRun> Text { get; init; }
}

public sealed class RichHeadingBlock : RichBlock
{
    [JsonPropertyName("level")] public int Level { get; init; }
    [JsonPropertyName("text")] public required IReadOnlyList<RichTextRun> Text { get; init; }
}

public sealed class RichListBlock : RichBlock
{
    [JsonPropertyName("ordered")] public bool Ordered { get; init; }
    [JsonPropertyName("items")] public required IReadOnlyList<IReadOnlyList<RichBlock>> Items { get; init; }
}

public sealed class RichTableBlock : RichBlock
{
    [JsonPropertyName("rows")] public required IReadOnlyList<IReadOnlyList<RichTableCell>> Rows { get; init; }
}

public sealed class RichTableCell
{
    [JsonPropertyName("text")] public required IReadOnlyList<RichTextRun> Text { get; init; }
    [JsonPropertyName("header")] public bool Header { get; init; }
    [JsonPropertyName("align")] public string? Align { get; init; } // "left"|"center"|"right"|null
}

public sealed class RichCodeBlock : RichBlock
{
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
}

public sealed class RichQuoteBlock : RichBlock
{
    [JsonPropertyName("blocks")] public required IReadOnlyList<RichBlock> Blocks { get; init; }
}

public sealed class RichDividerBlock : RichBlock { }

public sealed class RichMathBlock : RichBlock
{
    [JsonPropertyName("text")] public required string Text { get; init; } // LaTeX source
}

public sealed class RichThinkingBlock : RichBlock
{
    [JsonPropertyName("text")] public string? Text { get; init; } // state affordance, e.g. "Thinking…"
}

/// <summary>An inline styled text run.</summary>
public sealed class RichTextRun
{
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("bold")] public bool? Bold { get; init; }
    [JsonPropertyName("italic")] public bool? Italic { get; init; }
    [JsonPropertyName("underline")] public bool? Underline { get; init; }
    [JsonPropertyName("strikethrough")] public bool? Strikethrough { get; init; }
    [JsonPropertyName("code")] public bool? Code { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; } // link target, http/https only
}
```

**Step 4: Run to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RichModelsSerializationTests"`
Expected: PASS. If the discriminator emits differently than `"type":"paragraph"`, fix the test/attributes to match Task 1's schema — do not force a shape the API rejects.

**Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telegram/RichMessages/RichModels.cs src/agent/OpenAgent.Tests/Telegram/RichModelsSerializationTests.cs
git commit -m "feat(telegram): add Rich Message DTO model"
```

---

## Task 3: Markdown → RichMessage converter — paragraphs, headings, inline runs

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMarkdownConverter.cs`
- Test: `src/agent/OpenAgent.Tests/TelegramRichConverterTests.cs`

Add a new `ToRichMessage(string markdown) : RichMessage` alongside the existing `ToTelegramHtml` (keep `ToTelegramHtml` and `ChunkMarkdown` for the fallback path — do not delete). Enable the pipe-table extension on the Markdig pipeline.

**Step 1: Failing test**

```csharp
using OpenAgent.Channel.Telegram;
using OpenAgent.Channel.Telegram.RichMessages;

namespace OpenAgent.Tests;

public class TelegramRichConverterTests
{
    [Fact]
    public void Heading_maps_to_heading_block_with_level()
    {
        var msg = TelegramMarkdownConverter.ToRichMessage("## Title");
        var h = Assert.IsType<RichHeadingBlock>(Assert.Single(msg.Blocks));
        Assert.Equal(2, h.Level);
        Assert.Equal("Title", string.Concat(h.Text.Select(r => r.Text)));
    }

    [Fact]
    public void Paragraph_bold_and_italic_map_to_runs()
    {
        var msg = TelegramMarkdownConverter.ToRichMessage("normal **b** and *i*");
        var p = Assert.IsType<RichParagraphBlock>(Assert.Single(msg.Blocks));
        Assert.Contains(p.Text, r => r.Text == "b" && r.Bold == true);
        Assert.Contains(p.Text, r => r.Text == "i" && r.Italic == true);
    }
}
```

**Step 2: Run — expect FAIL** (`ToRichMessage` missing).
Run: `dotnet test --filter "FullyQualifiedName~TelegramRichConverterTests"`

**Step 3: Implement** — add the pipe-table extension to `Pipeline`, and a block/inline walk that mirrors the existing `RenderBlock`/`RenderInline` structure but emits DTOs. Reuse `GetEmphasisTags` logic to set `Bold`/`Italic`/`Strikethrough` on `RichTextRun`, `CodeInline`→`Code=true`, `LinkInline`→`Url` (http/https only, same guard as `RenderLink`). Headings → `RichHeadingBlock { Level = heading.Level }`. Paragraphs → `RichParagraphBlock`.

**Step 4: Run — expect PASS.**

**Step 5: Commit**
```bash
git add -A && git commit -m "feat(telegram): markdown->rich paragraphs, headings, inline runs"
```

---

## Task 4: Converter — lists, code, block quotes, dividers, tables, math

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMarkdownConverter.cs`
- Test: `src/agent/OpenAgent.Tests/TelegramRichConverterTests.cs`

**Step 1: Failing tests** (one per element; add incrementally)

```csharp
[Fact]
public void Ordered_list_maps_to_list_block()
{
    var msg = TelegramMarkdownConverter.ToRichMessage("1. one\n2. two");
    var list = Assert.IsType<RichListBlock>(Assert.Single(msg.Blocks));
    Assert.True(list.Ordered);
    Assert.Equal(2, list.Items.Count);
}

[Fact]
public void Fenced_code_preserves_language_and_text()
{
    var msg = TelegramMarkdownConverter.ToRichMessage("```py\nx=1\n```");
    var code = Assert.IsType<RichCodeBlock>(Assert.Single(msg.Blocks));
    Assert.Equal("py", code.Language);
    Assert.Equal("x=1", code.Text.TrimEnd());
}

[Fact]
public void Pipe_table_maps_to_table_with_header_row()
{
    var md = "| A | B |\n|---|---|\n| 1 | 2 |";
    var msg = TelegramMarkdownConverter.ToRichMessage(md);
    var table = Assert.IsType<RichTableBlock>(Assert.Single(msg.Blocks));
    Assert.True(table.Rows[0][0].Header);
    Assert.Equal("1", string.Concat(table.Rows[1][0].Text.Select(r => r.Text)));
}

[Fact]
public void Blockquote_and_divider_map()
{
    var msg = TelegramMarkdownConverter.ToRichMessage("> quoted\n\n---");
    Assert.Contains(msg.Blocks, b => b is RichQuoteBlock);
    Assert.Contains(msg.Blocks, b => b is RichDividerBlock);
}
```

**Step 2: Run — expect FAIL.**

**Step 3: Implement** the remaining block cases: `ListBlock`→`RichListBlock` (each item's children recursively converted), `FencedCodeBlock`/`CodeBlock`→`RichCodeBlock`, `QuoteBlock`→`RichQuoteBlock`, `ThematicBreakBlock`→`RichDividerBlock`, Markdig `Table`→`RichTableBlock` (map `TableRow.IsHeader`→`Header`, column alignment→`Align`). For math: if the verified schema supports it, map `$$…$$`/`$…$` (enable Markdig math extension) to `RichMathBlock` / inline math run; otherwise leave math as literal text and note it in the design doc as deferred.

**Step 4: Run — expect PASS.**

**Step 5: Commit**
```bash
git add -A && git commit -m "feat(telegram): markdown->rich lists, code, tables, quotes, dividers, math"
```

---

## Task 5: `ITelegramSender` — rich send methods (raw HTTP)

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/ITelegramSender.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramBotClientSender.cs`
- Test: `src/agent/OpenAgent.Tests/Telegram/TelegramBotClientSenderTests.cs` (create if absent; otherwise extend)

**Step 1:** Add to `ITelegramSender`:
```csharp
/// <summary>Sends a complete Rich Message (Bot API 10.1). Returns the Telegram message ID.</summary>
Task<int> SendRichMessageAsync(ChatId chatId, RichMessages.RichMessage message, CancellationToken ct);

/// <summary>Streams a partial Rich Message draft (Bot API 10.1).</summary>
Task<DraftResult> SendRichDraftAsync(ChatId chatId, long draftId, RichMessages.RichMessage message, CancellationToken ct);
```

**Step 2: Failing test** — a WireMock/`HttpMessageHandler` stub (mirror any existing HttpClient test pattern) asserting `SendRichMessageAsync` POSTs to `sendRichMessage` with a `rich_message` body and returns the parsed `message_id`; and that a non-200 yields `DraftResult.Ok == false` with `retry_after` parsed. If no HttpClient test harness exists, inject a custom `HttpMessageHandler` via an internal test constructor on `TelegramBotClientSender`.

**Step 3: Implement** in `TelegramBotClientSender` using the existing `_httpClient`, mirroring `SendDraftAsync`: serialize the `RichMessage` with `RichMessage.JsonOptions`, POST `{ chat_id, rich_message }` to `sendRichMessage` (parse `result.message_id`) and `{ chat_id, draft_id, rich_message }` to `sendRichMessageDraft` (reuse the exact draft correlation confirmed in Task 1). Reuse the same error-body/`retry_after` parsing already in `SendDraftAsync`.

**Step 4: Run — expect PASS.**
Run: `dotnet test --filter "FullyQualifiedName~TelegramBotClientSenderTests"`

**Step 5: Commit**
```bash
git add -A && git commit -m "feat(telegram): raw-HTTP sendRichMessage/sendRichMessageDraft"
```

---

## Task 6: Feature flag on `TelegramOptions`

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramOptions.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProviderFactory.cs` (parse the flag from connection config)
- Test: extend the factory tests if present.

Add `bool RichMessages { get; init; } = true;` to `TelegramOptions`, parsed from the connection `Config` (default true). This is the kill switch: a connection can disable rich sends without redeploy, and the handler checks it before attempting the rich path. Keep it a single bool — YAGNI.

**Commit:**
```bash
git add -A && git commit -m "feat(telegram): add RichMessages kill-switch to TelegramOptions"
```

---

## Task 7: Handler — final response uses rich, falls back to HTML

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs` (`SendFinalResponseAsync` :520, `SendWithRetryAsync` :562)
- Test: `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`

**Step 1: Failing tests** — with a fake `ITelegramSender`: (a) when `RichMessages` is on, the final answer calls `SendRichMessageAsync` with a converted `RichMessage`; (b) when `SendRichMessageAsync` throws, the handler falls back to `SendHtmlAsync`; (c) when the flag is off, it uses the HTML path directly; (d) the `[]` suppression and empty→"OK!" guards still hold.

**Step 2: Run — expect FAIL.**

**Step 3: Implement** — in `SendFinalResponseAsync`, when the flag is on, convert the full (un-chunked) `replyText` via `ToRichMessage` and send via a new `SendRichWithFallbackAsync` that tries `SendRichMessageAsync`, and on exception/`!Ok` falls back to the existing chunk+`ToTelegramHtml`+`SendWithRetryAsync` path. Preserve the `telegramMessageId` capture for reply-to tracking (use the rich send's returned id). Keep chunking only on the fallback path (rich size handling per Task 1).

**Step 4: Run — expect PASS.** Then run the full Telegram test set:
Run: `dotnet test --filter "FullyQualifiedName~Telegram"`

**Step 5: Commit**
```bash
git add -A && git commit -m "feat(telegram): send final answer as Rich Message with HTML fallback"
```

---

## Task 8: Handler — thinking-state rich draft

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs` (`RunDraftConsumerAsync` :347, thinking handling around `SendThinkingMessageAsync` :428, and the `ThinkingStarted`/`ThinkingStopped` handling in the stream loop)
- Test: `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`

**Step 1: Failing test** — on a turn that emits `ThinkingStarted`, the handler sends a rich draft containing a `RichThinkingBlock` (state text, e.g. "Thinking…"); it does **not** include internal tool names; on `ThinkingStopped`/first answer content the thinking draft is superseded by the answer draft/final. Answer streaming stays lightweight (existing plain `SendDraftAsync`), unchanged.

**Step 2: Run — expect FAIL.**

**Step 3: Implement** — when `RichMessages` is on, replace the thinking surface: on `ThinkingStarted`, send `SendRichDraftAsync(chatId, draftId, new RichMessage { Blocks = [ new RichThinkingBlock { Text = "Thinking…" } ] })`. Leave the answer draft path (`RunDraftConsumerAsync` plain text) as-is. When the flag is off, keep the current `SendThinkingMessageAsync` HTML blockquote behavior.

**Step 4: Run — expect PASS.**

**Step 5: Commit**
```bash
git add -A && git commit -m "feat(telegram): native thinking block during thinking phase"
```

---

## Task 9: Full regression + manual verification

**Steps:**
1. `dotnet test` (full suite). Expected: green except the known parallel `WebApplicationFactory` flake (re-run any endpoint-test failures in isolation to confirm they pass — see CLAUDE.md CI-flake note). The new Telegram tests must pass deterministically.
2. Reconcile the design doc: mark verified schema, note any elements deferred (e.g. math if unsupported).
3. Manual: point a test bot connection at a build with `RichMessages=true`, send prompts that produce a table, a fenced code block, a heading + list, and a tool-using turn (to see the thinking block). Confirm native rendering and that a forced failure (temporarily break the rich payload) falls back to HTML cleanly. Use `@ superpowers:verification-before-completion` before claiming done.
4. Commit any doc updates.

**Commit:**
```bash
git add -A && git commit -m "docs: finalize Rich Messages schema notes and deferred items"
```

---

## Out of scope (do NOT implement here)

- Expressive-only vocabulary (collapsible, spoiler, pull-quote, media, maps) + the teaching mechanism (skill / system-prompt primer).
- Progressive *rich* answer streaming (block-boundary flushing) — v1 streams lightweight and only the final answer is rich.
- WhatsApp / other channels.

## Notes for the executor

- **Task 1 is blocking and authoritative.** If any property/discriminator/method name in Tasks 2–8 contradicts Task 1's verified schema, the verified schema wins — change the code, not the API.
- Keep `ToTelegramHtml` and `ChunkMarkdown` intact — they are the fallback.
- The agent authors nothing new in this plan; do not touch system prompt / skills / providers.
- Follow existing test style in `TelegramMessageHandlerTests` and `TelegramMarkdownConverterTests`.
