# WhatsApp Stanza ID Round-Trip Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plumb the real Baileys stanza ID back through the bridge protocol so WhatsApp assistant messages get a meaningful `ChannelMessageId`, making reply-to-bot lookups work in `BuildChatMessages` / `BuildMessages` (the same way they already work for Telegram).

**Architecture:** The .NET ↔ Node bridge protocol gains a single new event type, `sent`, that the bridge always emits after handling a `send` command — carrying the resulting stanza ID on success, or an error message on failure. The .NET side gates each `SendTextAsync` call with a `SemaphoreSlim`: at most one send-and-wait round-trip is in flight per `WhatsAppNodeProcess` instance. A single `TaskCompletionSource<string?>` field tracks the pending send; the dispatcher resolves it when the matching `sent` event arrives. `IWhatsAppSender.SendTextAsync` becomes `Task<string?>` returning the stanza ID. `WhatsAppMessageHandler` captures the first chunk's stanza ID and back-fills `Message.ChannelMessageId` via `UpdateChannelMessageId` — mirroring the Telegram pattern at `TelegramMessageHandler.cs:511-533`.

**Tech Stack:** C# (.NET 10, `SemaphoreSlim`, `TaskCompletionSource<string?>`), Node.js (Baileys `sock.sendMessage` returns a promise resolving to the sent message), xUnit.

**Branch:** Extends the existing `feat/reply-quoting` branch — this completes the feature (without it, WhatsApp reply-to-bot silently no-ops).

---

## File Structure

**Will be modified:**
- `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js` — `case "send"` block: capture `sock.sendMessage`'s result, emit `{type:"sent", id:"..."}` on success or `{type:"sent", message:"..."}` on error.
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs` — add `SendTextAndWaitAsync` method (semaphore-gated, awaits a TCS with timeout). The existing `OnEvent` callback dispatches `sent` events to the pending TCS. Update doc comments on the `Id` and `Message` fields of `NodeEvent` to mention the `sent` event-type reuse.
- `src/agent/OpenAgent.Channel.WhatsApp/IWhatsAppSender.cs` — change `Task SendTextAsync(...)` to `Task<string?> SendTextAsync(...)`.
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs` — `WhatsAppNodeProcessSender.SendTextAsync` returns the awaited stanza ID.
- `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs` — capture first chunk's stanza ID; replace the `"whatsapp:{chatId}"` placeholder with the real ID.
- `src/agent/OpenAgent.Tests/Fakes/FakeWhatsAppSender.cs` — return a configurable stanza ID; track which calls produced which IDs.
- `src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs` — add 2 parse tests for the `sent` event (success + error).
- `src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs` — add 1 test that verifies `UpdateChannelMessageId` is called with the real stanza ID returned by the sender.

**Why no new files:** Each change is small and lives naturally in an existing file. Adding a separate `WhatsAppSendTracker` class would be overkill — the lock + TCS pattern is ~25 lines on `WhatsAppNodeProcess` and conceptually belongs there alongside the existing event dispatch.

---

## Task 1: Extend bridge protocol — emit `sent` event from JS

**Files:**
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js` (the `case "send":` block in the stdin command handler around line 250)
- Modify: `src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs`

The `case "send":` block currently fires `sock.sendMessage` and returns nothing. We change it to: capture the return value (which is the sent message including `key.id`), emit a `sent` event with the stanza ID, and on error emit a `sent` event carrying the error message. The bridge **always** emits `sent` after handling a send command — this is what the .NET side awaits.

- [ ] **Step 1: Add the failing parse tests on the .NET side**

The `NodeEvent` record already has `Id` (string?) and `Message` (string?) fields. The new `sent` event reuses both: `Id` for success, `Message` for error. No record changes needed; just verify parsing works and add tests.

In `src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs`, append two new tests inside the `WhatsAppNodeProcessTests` class:

```csharp
    [Fact]
    public void ParseLine_SentEvent_ParsesStanzaId()
    {
        var json = "{\"type\":\"sent\",\"id\":\"3EB0ABCD1234\"}";
        var evt = WhatsAppNodeProcess.ParseLine(json);
        Assert.NotNull(evt);
        Assert.Equal("sent", evt.Type);
        Assert.Equal("3EB0ABCD1234", evt.Id);
        Assert.Null(evt.Message);
    }

    [Fact]
    public void ParseLine_SentEventWithError_ParsesMessage()
    {
        var json = "{\"type\":\"sent\",\"message\":\"connection lost\"}";
        var evt = WhatsAppNodeProcess.ParseLine(json);
        Assert.NotNull(evt);
        Assert.Equal("sent", evt.Type);
        Assert.Null(evt.Id);
        Assert.Equal("connection lost", evt.Message);
    }
```

- [ ] **Step 2: Run the tests to verify they pass already**

Both fields exist on `NodeEvent`, so these tests should pass without any production code changes.

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~WhatsAppNodeProcessTests"`
Expected: PASS — all existing + 2 new tests green.

This is a deliberately-redundant safety check before touching JS that has no automated tests of its own. The .NET side parses what the bridge will emit.

- [ ] **Step 3: Modify the bridge's `case "send":` block**

In `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js`, the current block reads:

```javascript
        case "send":
          await sock.sendMessage(cmd.chatId, { text: cmd.text });
          break;
```

Replace with:

```javascript
        case "send":
          try {
            const result = await sock.sendMessage(cmd.chatId, { text: cmd.text });
            const id = result?.key?.id;
            if (typeof id === "string" && id.length > 0) {
              emit({ type: "sent", id });
            } else {
              emit({ type: "sent", message: "send returned no message id" });
            }
          } catch (sendErr) {
            console.error("send failed:", sendErr);
            emit({ type: "sent", message: String(sendErr?.message || sendErr) });
          }
          break;
```

`emit` already exists in the bridge — it writes a JSON line to stdout.

- [ ] **Step 4: Run the .NET test suite to confirm no .NET regressions**

Run: `cd src/agent && dotnet test`
Expected: all green. JS change doesn't affect .NET behavior yet (sender is still fire-and-forget — Task 2 changes that).

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs
git commit -m "feat(whatsapp-bridge): emit sent event with stanza ID after send"
```

---

## Task 2: Add send-and-wait on the .NET side (`WhatsAppNodeProcess.SendTextAndWaitAsync`)

**Files:**
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs`

Add a new public method `SendTextAndWaitAsync` that serializes sends with a `SemaphoreSlim` (instance field), registers a `TaskCompletionSource<string?>` (instance field), writes the send command, and awaits the TCS with a timeout. The existing event-dispatch code (the line-reading task that invokes `OnEvent`) intercepts `sent` events and resolves the pending TCS.

**Design notes for the implementer:**

- The lock is per-instance (`WhatsAppNodeProcess` is one-per-connection).
- Composing stays fire-and-forget — does NOT acquire the lock and does NOT wait for a response. The bridge does not emit any event for composing.
- Timeout: 30 seconds. On timeout, the TCS is faulted, semaphore released, method returns null. Caller treats null the same as a send error.
- Cancellation token is observed during the await.
- A `sent` event arriving with no pending TCS is logged at warn level and ignored. (Shouldn't happen but defensive.)
- The TCS field is updated under the semaphore so there's no race between "register TCS" and "dispatcher reads TCS".

- [ ] **Step 1: Read `WhatsAppNodeProcess.cs` to understand the existing event-handling structure**

You need to know where line-reading happens (the stdout task that calls `ParseLine` and invokes `OnEvent`). Your dispatcher hook for `sent` events should integrate cleanly with the existing flow.

- [ ] **Step 2: Add fields and the new method**

Add these private fields to `WhatsAppNodeProcess`:

```csharp
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TaskCompletionSource<string?>? _pendingSend;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
```

Add the new public method (place it near `WriteAsync`, the existing send-command writer):

```csharp
    /// <summary>
    /// Sends a text message and awaits the bridge's <c>sent</c> response, returning the
    /// resulting stanza ID (or null on error/timeout). Sends are serialized per process
    /// instance — at most one send-and-wait is in flight at a time.
    /// </summary>
    public async Task<string?> SendTextAndWaitAsync(string chatId, string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSend = tcs;

            await WriteAsync(FormatSendCommand(chatId, text));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SendTimeout);
            using (timeoutCts.Token.Register(() => tcs.TrySetResult(null)))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _pendingSend = null;
            _sendLock.Release();
        }
    }
```

- [ ] **Step 3: Hook the dispatcher to resolve the TCS on `sent` events**

Locate the existing line-reading loop where `ParseLine` is called and `OnEvent` is invoked. **Before** invoking `OnEvent` (so external subscribers don't see the synthetic `sent` events), check for `sent` and complete the pending TCS:

```csharp
        if (evt.Type == "sent")
        {
            var pending = Interlocked.Exchange(ref _pendingSend, null);
            if (pending is null)
            {
                _logger.LogWarning("WhatsApp bridge emitted 'sent' with no pending send (id={Id}, message={Message})",
                    evt.Id, evt.Message);
            }
            else
            {
                pending.TrySetResult(evt.Id);
                if (evt.Message is not null)
                    _logger.LogWarning("WhatsApp send returned error: {Message}", evt.Message);
            }
            continue; // do not propagate to OnEvent
        }
```

(The exact insertion point and the surrounding loop variable names depend on the existing structure — read the file and adapt.)

- [ ] **Step 4: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: all green. No new tests yet — the new method has no callers outside the sender, which Task 3 updates.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs
git commit -m "feat(whatsapp): add semaphore-gated SendTextAndWaitAsync to bridge process"
```

---

## Task 3: Update `IWhatsAppSender`, `WhatsAppNodeProcessSender`, and `WhatsAppMessageHandler`

**Files:**
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/IWhatsAppSender.cs`
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs` (the private `WhatsAppNodeProcessSender` class around line 440)
- Modify: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs`
- Modify: `src/agent/OpenAgent.Tests/Fakes/FakeWhatsAppSender.cs`

Change `SendTextAsync`'s return type to `Task<string?>`, propagate the stanza ID from the new `SendTextAndWaitAsync`, capture the first chunk's ID in the handler, and replace the synthetic `"whatsapp:{chatId}"` placeholder with the real ID.

- [ ] **Step 1: Change the interface**

In `src/agent/OpenAgent.Channel.WhatsApp/IWhatsAppSender.cs`, change:

```csharp
    /// <summary>Sends a text message to the chat.</summary>
    Task SendTextAsync(string chatId, string text);
```

to:

```csharp
    /// <summary>
    /// Sends a text message to the chat and returns the resulting Baileys stanza ID.
    /// Returns null when the send failed or timed out.
    /// </summary>
    Task<string?> SendTextAsync(string chatId, string text);
```

- [ ] **Step 2: Update the concrete sender**

In `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs`, the `WhatsAppNodeProcessSender` class around line 450 currently reads:

```csharp
        public Task SendTextAsync(string chatId, string text) =>
            _process.WriteAsync(WhatsAppNodeProcess.FormatSendCommand(chatId, text));
```

Replace with:

```csharp
        public Task<string?> SendTextAsync(string chatId, string text) =>
            _process.SendTextAndWaitAsync(chatId, text, CancellationToken.None);
```

(`CancellationToken.None` is acceptable because the channel provider doesn't currently plumb a token down to the sender. If a future change wires one through, the signature already accepts one.)

- [ ] **Step 3: Update `WhatsAppMessageHandler` to capture the first chunk's stanza ID**

In `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs`, the existing send + back-fill block reads:

```csharp
        // Chunk and send
        var chunks = WhatsAppMarkdownConverter.ChunkText(whatsAppText, WhatsAppMaxMessageLength);
        foreach (var chunk in chunks)
        {
            await sender.SendTextAsync(chatId, chunk);
        }

        // Update assistant message with channel message ID if available
        if (assistantMessageId is not null)
        {
            _store.UpdateChannelMessageId(assistantMessageId, $"whatsapp:{chatId}");
            _logger?.LogDebug("Updated assistant message {MessageId} for chat {ChatId}", assistantMessageId, chatId);
        }
```

Replace with:

```csharp
        // Chunk and send. Keep the first chunk's stanza ID — that's what users will reply to.
        var chunks = WhatsAppMarkdownConverter.ChunkText(whatsAppText, WhatsAppMaxMessageLength);
        string? firstStanzaId = null;
        foreach (var chunk in chunks)
        {
            var stanzaId = await sender.SendTextAsync(chatId, chunk);
            firstStanzaId ??= stanzaId;
        }

        // Back-fill the assistant message with the real Baileys stanza ID so reply-to-bot
        // lookups resolve correctly in BuildChatMessages / BuildMessages.
        if (assistantMessageId is not null && firstStanzaId is not null)
        {
            _store.UpdateChannelMessageId(assistantMessageId, firstStanzaId);
            _logger?.LogDebug("Updated assistant message {MessageId} for chat {ChatId} with stanza {StanzaId}",
                assistantMessageId, chatId, firstStanzaId);
        }
```

(The synthetic `"whatsapp:{chatId}"` value is fully removed.)

- [ ] **Step 4: Update `FakeWhatsAppSender` to satisfy the new signature**

Read `src/agent/OpenAgent.Tests/Fakes/FakeWhatsAppSender.cs`. Change `SendTextAsync` to return `Task<string?>`. Default behavior: return a synthesized stanza ID like `$"FAKE-{Guid.NewGuid():N}"` (or expose a configurable next-id field). Track returned IDs alongside the existing call recording.

A reasonable shape:

```csharp
    private int _nextSendId;

    public List<(string ChatId, string Text, string StanzaId)> TextCalls { get; } = new();

    public Task<string?> SendTextAsync(string chatId, string text)
    {
        var stanzaId = $"FAKE-{++_nextSendId}";
        TextCalls.Add((chatId, text, stanzaId));
        return Task.FromResult<string?>(stanzaId);
    }
```

(Adapt to the file's existing shape — preserve other fields/methods.)

- [ ] **Step 5: Add a handler test that verifies `UpdateChannelMessageId` is called with the real stanza ID**

In `src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs`, add a new test inside the existing `WhatsAppMessageHandlerTests` class. Use the existing `InMemoryConversationStore` and `FakeWhatsAppSender` patterns visible in surrounding tests:

```csharp
    [Fact]
    public async Task HandleMessageAsync_PopulatesAssistantChannelMessageIdWithStanzaId()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeWhatsAppTextProvider("Hi from bot");
        var sender = new FakeWhatsAppSender(); // returns sequential stanza IDs starting "FAKE-1"
        var handler = new WhatsAppMessageHandler(
            store,
            new FakeConnectionStore(ConnectionId),
            _ => provider,
            ConnectionId,
            TestAgentConfig);

        var inbound = new NodeEvent { Type = "message", Id = "INBOUND-1", ChatId = "+45@s.whatsapp.net", Text = "hello" };
        await handler.HandleMessageAsync(sender, inbound, CancellationToken.None);

        var conversation = store.FindChannelConversation("whatsapp", ConnectionId, "+45@s.whatsapp.net");
        Assert.NotNull(conversation);
        var messages = store.GetMessages(conversation.Id);
        var assistant = Assert.Single(messages, m => m.Role == "assistant");
        Assert.Equal("FAKE-1", assistant.ChannelMessageId);
    }
```

(Adapt: use the existing test fixture's constants and the existing `FakeWhatsAppTextProvider` if present, otherwise use whatever streaming-text fake the rest of the file uses. The test asserts the assistant message's `ChannelMessageId` is the real stanza ID returned by the sender, NOT the old `whatsapp:+45@s.whatsapp.net` placeholder. If existing tests construct `WhatsAppMessageHandler` differently, follow their pattern.)

- [ ] **Step 6: Run the full test suite**

Run: `cd src/agent && dotnet test`
Expected: all green. The new test passes; existing handler tests still pass (their assertions don't touch `ChannelMessageId`).

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/IWhatsAppSender.cs \
        src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs \
        src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs \
        src/agent/OpenAgent.Tests/Fakes/FakeWhatsAppSender.cs \
        src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs
git commit -m "feat(whatsapp): back-fill assistant ChannelMessageId with real stanza ID"
```

---

## Task 4: Final verification

- [ ] **Step 1: Full build + test sweep**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: all green.

- [ ] **Step 2: Confirm the synthetic placeholder is fully gone**

Run: `git -C <repo-root> grep "\"whatsapp:\""`
Expected: no matches in `src/`. Documentation/plan files may still mention it historically — those are fine.

- [ ] **Step 3: Confirm three new commits on top of the prior reply-quoting work**

Run: `git log --oneline master..HEAD`
Expected: the original 8 reply-quoting commits plus 3 new ones from Tasks 1–3 of this plan.

- [ ] **Step 4: Manually verify (optional, requires live WhatsApp pairing)**

If a paired WhatsApp test instance is available, send a message to the bot, wait for the response, then long-press the bot's reply and reply to it. The agent's next prompt should contain the bot's reply text as a `> ...` blockquote line above the new user message.

If no live pairing is available, skip — the unit test added in Task 3 covers the back-fill behavior, and the JS change is small and visible in `git show`.

---

## Self-Review Notes

- **Spec coverage:** Plan covers (1) bridge protocol extension, (2) .NET-side semaphore-gated send-and-wait, (3) interface + handler + fake updates with a new integration test. The whole gap surfaced by the prior code review is closed.
- **Edge cases handled:** send error in JS (caught, emitted as `sent` with `message`), timeout (30s, returns null), `sent` arriving with no pending TCS (logged warn, ignored), cancellation token (linked to the timeout, observed during await), composing stays unchanged (no wait, no lock).
- **Concurrency model:** at most one send-and-wait per `WhatsAppNodeProcess` instance. Different connections have their own instances. Multiple chats in the same connection are serialized — acceptable for the current load profile.
- **Why no test for `WhatsAppNodeProcess.SendTextAndWaitAsync` directly:** that method is integration-y (requires a real Node child process with stdin/stdout). The behavior is exercised end-to-end via the handler test in Task 3 Step 5 with a fake sender. If a future need arises, the dispatcher logic (resolving a TCS on `sent` event) could be extracted to a tiny pure helper for unit testing, but YAGNI for now.
- **Type consistency:** `Task<string?>` used uniformly across `IWhatsAppSender`, `WhatsAppNodeProcessSender`, `FakeWhatsAppSender`, and `WhatsAppNodeProcess.SendTextAndWaitAsync`.
