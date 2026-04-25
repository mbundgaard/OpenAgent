# Reply Quoting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a user replies to a specific earlier message on Telegram or WhatsApp, the LLM sees the quoted text inline so it can disambiguate which message is being replied to.

**Architecture:** Quoted text is rendered at LLM-context-build time, never persisted to `Message.Content`. Both text providers (Azure OpenAI, Anthropic Subscription) build a `ChannelMessageId → Content` lookup from the stored messages once per call, and when emitting a user (or assistant) message that has `ReplyToChannelMessageId` set, prepend a markdown blockquote with the looked-up content. WhatsApp's Baileys Node bridge is extended to extract the `contextInfo.stanzaId` field that Baileys provides on replies, and the .NET handler maps it to `Message.ReplyToChannelMessageId` (Telegram already does this).

**Tech Stack:** C# (.NET 10), xUnit, Node.js (Baileys bridge — no test framework, change validated via NodeEvent parse test in xUnit).

---

## File Structure

**Will be created:**
- `src/agent/OpenAgent.Models/Common/ReplyQuoteFormatter.cs` — pure static helper that renders the blockquote-prefix format. Lives in Models so both LLM provider projects can reference it.
- `src/agent/OpenAgent.Tests/ReplyQuoteFormatterTests.cs` — exhaustive unit tests for the formatter (pure function, easy to TDD).

**Will be modified:**
- `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs` — replace existing `[Reply to Msg: {id}]` prefix (lines 471–475) with formatter call; build a `ChannelMessageId → Content` lookup at the top of `BuildChatMessages`.
- `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs` — add the same lookup + formatter call inside `BuildMessages` at line 574 (currently emits `Content = msg.Content ?? ""` with no reply handling).
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs` — add `ReplyTo` property to `NodeEvent` record.
- `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js` — extract `contextInfo.stanzaId` from `extendedTextMessage` and include `replyTo` in the emitted message event.
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs` — set `ReplyToChannelMessageId` on the user message from `message.ReplyTo`.
- `src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs` — add a parse test for the new `replyTo` field.

**Why not a single shared helper for the lookup-build?** Each provider already iterates `storedMessages` once for its own message-shape transformation. Adding a small dictionary-build at the top is one extra `foreach` and avoids forcing the providers to share more than the format string.

---

## Task 1: Add ReplyQuoteFormatter helper (TDD)

**Files:**
- Create: `src/agent/OpenAgent.Models/Common/ReplyQuoteFormatter.cs`
- Test: `src/agent/OpenAgent.Tests/ReplyQuoteFormatterTests.cs`

The formatter takes the user's actual content and the replied-to content (or null if the replied-to message isn't available), and returns the rendered string to send to the LLM.

**Format specification:**
- If quoted content is null: return `userContent` unchanged. The caller didn't find the replied-to message (compacted out, etc.), so no quote is emitted.
- If quoted content is non-null:
  - Collapse whitespace (newlines, tabs) to single spaces, then trim.
  - Truncate to 200 chars; if truncated, append `…` (single Unicode ellipsis, U+2026).
  - Prefix with `> ` (markdown blockquote).
  - Append a blank line, then the user's content.
- If userContent is null or empty: still emit the quote line (e.g. `> earlier text\n\n` — represents a content-less reply, e.g. an emoji-only reply). The provider will likely have substituted empty for null already, but the formatter doesn't crash on null.

**Truncation length 200** is a deliberate, tested constant — long enough to be meaningful, short enough that long quoted threads don't bloat the prompt.

- [ ] **Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/ReplyQuoteFormatterTests.cs`:

```csharp
using OpenAgent.Models.Common;

namespace OpenAgent.Tests;

public class ReplyQuoteFormatterTests
{
    [Fact]
    public void Format_NullQuoted_ReturnsContentUnchanged()
    {
        var result = ReplyQuoteFormatter.Format("hello", null);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Format_EmptyQuoted_ReturnsContentUnchanged()
    {
        var result = ReplyQuoteFormatter.Format("hello", "");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Format_ShortQuoted_PrependsBlockquoteAndBlankLine()
    {
        var result = ReplyQuoteFormatter.Format("got it", "Original message");
        Assert.Equal("> Original message\n\ngot it", result);
    }

    [Fact]
    public void Format_QuotedWithNewlines_CollapsesToSingleLine()
    {
        var result = ReplyQuoteFormatter.Format("ok", "line one\nline two\nline three");
        Assert.Equal("> line one line two line three\n\nok", result);
    }

    [Fact]
    public void Format_QuotedWithTabsAndMultipleSpaces_CollapsesWhitespace()
    {
        var result = ReplyQuoteFormatter.Format("ok", "a\t\tb   c");
        Assert.Equal("> a b c\n\nok", result);
    }

    [Fact]
    public void Format_QuotedLongerThan200Chars_TruncatesWithEllipsis()
    {
        var quoted = new string('a', 250);
        var result = ReplyQuoteFormatter.Format("ok", quoted);
        var expectedQuote = new string('a', 200) + "…";
        Assert.Equal($"> {expectedQuote}\n\nok", result);
    }

    [Fact]
    public void Format_QuotedExactly200Chars_NoEllipsis()
    {
        var quoted = new string('a', 200);
        var result = ReplyQuoteFormatter.Format("ok", quoted);
        Assert.Equal($"> {quoted}\n\nok", result);
    }

    [Fact]
    public void Format_NullContent_StillEmitsQuoteWithEmptyTrailer()
    {
        var result = ReplyQuoteFormatter.Format(null, "earlier");
        Assert.Equal("> earlier\n\n", result);
    }

    [Fact]
    public void Format_EmptyContent_StillEmitsQuoteWithEmptyTrailer()
    {
        var result = ReplyQuoteFormatter.Format("", "earlier");
        Assert.Equal("> earlier\n\n", result);
    }

    [Fact]
    public void Format_QuotedWithLeadingTrailingWhitespace_Trimmed()
    {
        var result = ReplyQuoteFormatter.Format("ok", "   earlier   ");
        Assert.Equal("> earlier\n\nok", result);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~ReplyQuoteFormatterTests"`
Expected: FAIL — `ReplyQuoteFormatter` type not found / does not exist.

- [ ] **Step 3: Implement ReplyQuoteFormatter**

Create `src/agent/OpenAgent.Models/Common/ReplyQuoteFormatter.cs`:

```csharp
using System.Text.RegularExpressions;

namespace OpenAgent.Models.Common;

/// <summary>
/// Renders a user (or assistant) message that is a reply to an earlier channel message
/// as a markdown blockquote prefix followed by the actual message content. The LLM sees
/// the quoted text inline and can disambiguate which earlier message is being replied to.
/// Output is never persisted — this runs at LLM-context-build time only.
/// </summary>
public static class ReplyQuoteFormatter
{
    private const int MaxQuotedLength = 200;
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Formats a reply with a quoted prefix. When <paramref name="quotedContent"/> is null
    /// or empty, returns <paramref name="content"/> unchanged (no quote available, e.g. the
    /// replied-to message was compacted out of context).
    /// </summary>
    /// <param name="content">The actual message content the user typed.</param>
    /// <param name="quotedContent">The replied-to message content, or null if unavailable.</param>
    /// <returns>The content prefixed with a blockquote line, or unchanged if no quote.</returns>
    public static string Format(string? content, string? quotedContent)
    {
        if (string.IsNullOrEmpty(quotedContent))
            return content ?? "";

        // Collapse all whitespace runs (newlines, tabs, multiple spaces) to a single space, trim.
        var collapsed = WhitespaceRun.Replace(quotedContent, " ").Trim();

        // Truncate to MaxQuotedLength, append ellipsis if cut.
        var quoted = collapsed.Length > MaxQuotedLength
            ? collapsed[..MaxQuotedLength] + "…"
            : collapsed;

        return $"> {quoted}\n\n{content ?? ""}";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~ReplyQuoteFormatterTests"`
Expected: PASS — all 10 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Models/Common/ReplyQuoteFormatter.cs src/agent/OpenAgent.Tests/ReplyQuoteFormatterTests.cs
git commit -m "feat(models): add ReplyQuoteFormatter for inline reply-quoting"
```

---

## Task 2: Wire formatter into Azure OpenAI provider, replacing the bare-ID prefix

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:389-488` (`BuildChatMessages` method)

The existing code at lines 471–475 prepends `[Reply to Msg: {id}] ` when `ReplyToChannelMessageId` is non-null. That's the ID-based approach we're replacing. We:
1. Build a `Dictionary<string, string?>` keyed by `ChannelMessageId` from `storedMessages` once at the top.
2. Replace the existing prefix branch with `ReplyQuoteFormatter.Format(msg.Content, lookup[msg.ReplyToChannelMessageId])`.
3. The lookup may not contain the replied-to ID (it was compacted out, etc.) — `TryGetValue` and pass null to the formatter, which returns the content unchanged.

- [ ] **Step 1: Read the current BuildChatMessages method to confirm context**

Read `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs` lines 389–488 to confirm structure before editing.

- [ ] **Step 2: Modify BuildChatMessages**

Replace the existing block (lines 411–480 region — the storedMessages iteration plus the Regular message handling). Specifically:

After the line `var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);` (line 413), insert the lookup build:

```csharp
        // Build channel-message-id -> content lookup for inline reply-quote rendering.
        // Keyed by ChannelMessageId so we can resolve ReplyToChannelMessageId at render time.
        var channelMessageContent = new Dictionary<string, string?>();
        foreach (var stored in storedMessages)
        {
            if (stored.ChannelMessageId is { } cmid)
                channelMessageContent[cmid] = stored.Content;
        }
```

Then locate the existing Regular-message branch (around lines 469–479):

```csharp
            // Regular message (user, assistant text, or tool with id). For tool messages,
            // prefer the full on-disk result loaded via ToolResultRef.
            var content = msg.Role == "tool" && msg.FullToolResult is not null
                ? msg.FullToolResult
                : msg.ReplyToChannelMessageId is not null
                    ? $"[Reply to Msg: {msg.ReplyToChannelMessageId}] {msg.Content}"
                    : msg.Content;
            var chatMsg = new ChatMessage { Role = msg.Role, Content = content, Name = ChannelMessageName(msg) };
            if (msg.ToolCallId is not null)
                chatMsg.ToolCallId = msg.ToolCallId;
            chatMessages.Add(chatMsg);
```

Replace with:

```csharp
            // Regular message (user, assistant text, or tool with id). For tool messages,
            // prefer the full on-disk result loaded via ToolResultRef. For user/assistant
            // messages with a ReplyToChannelMessageId, render an inline blockquote of the
            // replied-to content so the LLM can disambiguate.
            string? content;
            if (msg.Role == "tool" && msg.FullToolResult is not null)
            {
                content = msg.FullToolResult;
            }
            else if (msg.ReplyToChannelMessageId is { } replyId
                     && channelMessageContent.TryGetValue(replyId, out var quoted))
            {
                content = ReplyQuoteFormatter.Format(msg.Content, quoted);
            }
            else
            {
                content = msg.Content;
            }
            var chatMsg = new ChatMessage { Role = msg.Role, Content = content, Name = ChannelMessageName(msg) };
            if (msg.ToolCallId is not null)
                chatMsg.ToolCallId = msg.ToolCallId;
            chatMessages.Add(chatMsg);
```

Add the `using` directive at the top of the file (after the other `OpenAgent.Models` usings):

```csharp
using OpenAgent.Models.Common;
```

(Verify it isn't already present. If `ReplyQuoteFormatter` is found by another existing using, skip.)

- [ ] **Step 3: Build and confirm no compilation errors**

Run: `cd src/agent && dotnet build OpenAgent.LlmText.OpenAIAzure`
Expected: SUCCESS, no warnings.

- [ ] **Step 4: Run all tests to confirm nothing regressed**

Run: `cd src/agent && dotnet test`
Expected: all green. No new tests yet for the wiring — Task 4 covers integration. The formatter unit tests remain green; the existing channel handler tests don't exercise BuildChatMessages directly.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs
git commit -m "feat(llm-azure): inline-quote replied-to messages instead of bare ID prefix"
```

---

## Task 3: Wire formatter into Anthropic Subscription provider

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:484-582` (`BuildMessages` method)

Anthropic provider currently has no reply handling. Same pattern as Task 2: build the lookup at the top of `BuildMessages`, then call `ReplyQuoteFormatter.Format` on the regular user/assistant emit at line 574.

- [ ] **Step 1: Read the current BuildMessages method to confirm context**

Read `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs` lines 484–582 to confirm structure before editing.

- [ ] **Step 2: Modify BuildMessages**

After the line `var storedMessages = agentLogic.GetMessages(conversation.Id, includeToolResultBlobs: true);` (around line 503), insert:

```csharp
        // Build channel-message-id -> content lookup for inline reply-quote rendering.
        var channelMessageContent = new Dictionary<string, string?>();
        foreach (var stored in storedMessages)
        {
            if (stored.ChannelMessageId is { } cmid)
                channelMessageContent[cmid] = stored.Content;
        }
```

Replace the regular-message emit at line 574:

```csharp
            // Regular user or assistant message
            result.Add(new AnthropicMessage { Role = msg.Role, Content = msg.Content ?? "" });
```

with:

```csharp
            // Regular user or assistant message. When ReplyToChannelMessageId resolves to
            // a known earlier message, render an inline blockquote so the LLM can
            // disambiguate which earlier message is being replied to.
            string content;
            if (msg.ReplyToChannelMessageId is { } replyId
                && channelMessageContent.TryGetValue(replyId, out var quoted))
            {
                content = ReplyQuoteFormatter.Format(msg.Content, quoted);
            }
            else
            {
                content = msg.Content ?? "";
            }
            result.Add(new AnthropicMessage { Role = msg.Role, Content = content });
```

Add the using directive at the top of the file (after the other `OpenAgent.Models` usings):

```csharp
using OpenAgent.Models.Common;
```

(Verify not already present.)

- [ ] **Step 3: Build and confirm no compilation errors**

Run: `cd src/agent && dotnet build OpenAgent.LlmText.AnthropicSubscription`
Expected: SUCCESS, no warnings.

- [ ] **Step 4: Run all tests to confirm nothing regressed**

Run: `cd src/agent && dotnet test`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs
git commit -m "feat(llm-anthropic): inline-quote replied-to messages in BuildMessages"
```

---

## Task 4: Add ReplyTo field to WhatsApp NodeEvent (TDD)

**Files:**
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs:15-60` (NodeEvent record)
- Modify: `src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs`

The Node bridge will start emitting a `replyTo` field on `message` events. Add a corresponding property on the `NodeEvent` record so .NET can deserialize it.

- [ ] **Step 1: Write the failing test**

Open `src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs` and append a test inside the `WhatsAppNodeProcessTests` class:

```csharp
    [Fact]
    public void ParseLine_MessageEventWithReplyTo_ParsesReplyToField()
    {
        var json = "{\"type\":\"message\",\"id\":\"ABC\",\"chatId\":\"+45@s.whatsapp.net\",\"text\":\"got it\",\"replyTo\":\"XYZ\"}";
        var evt = WhatsAppNodeProcess.ParseLine(json);
        Assert.NotNull(evt);
        Assert.Equal("ABC", evt.Id);
        Assert.Equal("XYZ", evt.ReplyTo);
    }

    [Fact]
    public void ParseLine_MessageEventWithoutReplyTo_HasNullReplyTo()
    {
        var json = "{\"type\":\"message\",\"id\":\"ABC\",\"chatId\":\"+45@s.whatsapp.net\",\"text\":\"hi\"}";
        var evt = WhatsAppNodeProcess.ParseLine(json);
        Assert.NotNull(evt);
        Assert.Null(evt.ReplyTo);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~WhatsAppNodeProcessTests"`
Expected: FAIL — `'NodeEvent' does not contain a definition for 'ReplyTo'`.

- [ ] **Step 3: Add ReplyTo property to NodeEvent**

Edit `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs`. Inside the `NodeEvent` record (between the existing `Timestamp` and `Reason` fields, or as the last field — pick a placement consistent with the surrounding doc comments):

```csharp
    /// <summary>The message ID this message is replying to (for type=message).
    /// Populated from Baileys <c>extendedTextMessage.contextInfo.stanzaId</c>; null when the
    /// message is not a reply.</summary>
    [JsonPropertyName("replyTo")]
    public string? ReplyTo { get; init; }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~WhatsAppNodeProcessTests"`
Expected: PASS — both new tests green, all existing tests still green.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs
git commit -m "feat(whatsapp): add ReplyTo field to NodeEvent for reply-to-message ID"
```

---

## Task 5: Extend Baileys bridge to extract reply-to stanzaId

**Files:**
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js:170-214` (messages.upsert handler)

Baileys exposes the replied-to message ID at `message.message.extendedTextMessage.contextInfo.stanzaId`. We extract it (when present) and include it as `replyTo` on the emitted event. Plain-text non-reply messages don't have `extendedTextMessage`, so the field is omitted in that case.

- [ ] **Step 1: Add the extraction helper**

In `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js`, after the existing `extractText` function (after line 75), add:

```javascript
/**
 * Extract the replied-to message ID, if this message is a reply.
 * Baileys exposes it at message.extendedTextMessage.contextInfo.stanzaId.
 * Returns undefined for non-replies and plain conversation messages.
 */
function extractReplyTo(message) {
  if (!message) {
    return undefined;
  }
  const stanzaId = message.extendedTextMessage?.contextInfo?.stanzaId;
  if (typeof stanzaId === "string" && stanzaId.length > 0) {
    return stanzaId;
  }
  return undefined;
}
```

- [ ] **Step 2: Wire extractReplyTo into the message emit**

In the `messages.upsert` handler (around line 201), replace the existing emit block:

```javascript
        emit({
          type: "message",
          id: key.id || undefined,
          chatId,
          from,
          pushName: msg.pushName || undefined,
          text,
          timestamp,
        });
```

with:

```javascript
        emit({
          type: "message",
          id: key.id || undefined,
          chatId,
          from,
          pushName: msg.pushName || undefined,
          text,
          replyTo: extractReplyTo(msg.message),
          timestamp,
        });
```

(`replyTo` is `undefined` when not a reply, which the JSON serializer omits — the .NET deserializer reads it as null.)

- [ ] **Step 3: Run all tests to confirm nothing regressed**

Run: `cd src/agent && dotnet test`
Expected: all green. No new .NET tests for this — the JS change is observable through Task 4's parse tests (the .NET side already accepts `replyTo`).

- [ ] **Step 4: Manually verify (optional, for the executing engineer)**

If a WhatsApp-paired test instance is available, send a message in WhatsApp, then long-press → Reply → type a reply, send. Inspect logs for `Message from chat ...` and confirm Baileys emitted `replyTo` (visible in the bridge stderr if logging is enabled, or via a debug breakpoint in `WhatsAppMessageHandler.HandleMessageAsync`). Skip if no live WhatsApp pairing — Task 4's parse tests cover the protocol shape.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js
git commit -m "feat(whatsapp-bridge): emit replyTo from extendedTextMessage.contextInfo"
```

---

## Task 6: Set ReplyToChannelMessageId in WhatsAppMessageHandler

**Files:**
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs:121-130` (user message construction)

Telegram already sets `ReplyToChannelMessageId` (TelegramMessageHandler.cs:143). WhatsApp doesn't yet — adding it now that NodeEvent has `ReplyTo`.

- [ ] **Step 1: Modify the user message construction**

In `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs`, locate the user message build (lines 121–129):

```csharp
        // Build user message
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = userText,
            ChannelMessageId = message.Id
        };
```

Replace with:

```csharp
        // Build user message. ReplyToChannelMessageId comes from Baileys
        // contextInfo.stanzaId — when set, the LLM-context builder will render the
        // replied-to message inline as a markdown blockquote.
        var userMessage = new Message
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversation.Id,
            Role = "user",
            Content = userText,
            ChannelMessageId = message.Id,
            ReplyToChannelMessageId = message.ReplyTo
        };
```

- [ ] **Step 2: Build and confirm no compilation errors**

Run: `cd src/agent && dotnet build OpenAgent.Channel.WhatsApp`
Expected: SUCCESS, no warnings.

- [ ] **Step 3: Run all tests to confirm nothing regressed**

Run: `cd src/agent && dotnet test`
Expected: all green. The existing `WhatsAppMessageHandlerTests` don't exercise replies but should keep passing — the field defaults to null when not a reply.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs
git commit -m "feat(whatsapp): pass ReplyTo through to Message.ReplyToChannelMessageId"
```

---

## Task 7: Final verification

- [ ] **Step 1: Full build + test sweep**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: all green. Both formatter unit tests, both new NodeEvent parse tests, and all pre-existing tests pass.

- [ ] **Step 2: Confirm git log shows six focused commits**

Run: `git log --oneline -10`
Expected: six commits matching the messages in Tasks 1–6, on top of the prior `52b717f refactor: drop projects/ in favor of skills/{name}/data/` commit.

- [ ] **Step 3: Skim diff for any stale `[Reply to Msg:` strings**

Run: `git grep "Reply to Msg"`
Expected: no matches (the old Azure prefix has been replaced; no stragglers in tests or docs).

If any match shows up in `docs/` (it might in a plan or a review note), evaluate whether it needs updating — those are historical and can stay.

---

## Self-Review Notes

- **Spec coverage:** Plan covers (1) the formatter, (2) Azure provider wiring, (3) Anthropic provider wiring, (4) WhatsApp `ReplyTo` capture in three layers (NodeEvent, JS bridge, handler). Telegram already captures the field — no change needed on the Telegram inbound side.
- **Edge cases handled:** quoted message compacted out (lookup miss → no quote, original content emitted); long quote (truncated at 200 chars + ellipsis); multi-line quote (collapsed to single line); null/empty content (graceful); replied-to message is from the assistant (lookup works the same).
- **Not in scope:** outbound reply marking on Telegram/WhatsApp (the agent could quote-reply to the user's specific message — that's a different feature). Web/REST/WebSocket channels (no reply concept there). UI rendering of replies in the app (separate frontend concern).
- **Type consistency:** `ReplyQuoteFormatter.Format(string?, string?)` signature used consistently in both Tasks 2 and 3.
