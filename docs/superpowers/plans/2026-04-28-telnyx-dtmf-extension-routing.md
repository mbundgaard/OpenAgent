# Telnyx DTMF Extension Routing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let inbound Telnyx callers route into a per-extension conversation by dialling `+4535150636,1` (comma-pause sends DTMF after answer). The agent greets immediately on a fresh per-call throwaway conversation; if a digit arrives within 8 seconds we swap to the extension conversation mid-session and discard the throwaway.

**Architecture:** Conversation creation moves from `TelnyxWebhookEndpoints.OnCallInitiated` into `TelnyxMediaBridge.RunAsync`. The bridge brings the realtime session up against a per-call throwaway conversation, opens an 8-second DTMF gate, and on the first digit performs a mid-flight swap via a new `IVoiceSession.RebindConversationAsync(Conversation)` method (OpenAI + Grok implement; Gemini throws `NotSupportedException` and the bridge degrades to "no swap"). DTMF webhooks dispatch via call-control-id; the registry gains a parallel index so both `EndCallTool` (conversation-id keyed) and the DTMF path (call-control-id keyed) can find the bridge.

**Tech Stack:** .NET 10, ASP.NET Core, System.Text.Json, xUnit + `WebApplicationFactory<Program>`. No new NuGet dependencies.

Spec reference: `docs/superpowers/specs/2026-04-28-telnyx-dtmf-extension-routing-design.md`.

Suggested PR split:

- **PR1 — Voice contract (Tasks 1–3).** `IVoiceSession.RebindConversationAsync` + per-provider implementations. Fully tested in isolation. Telnyx still routes by `from` until PR2 lands.
- **PR2 — Telnyx wiring (Tasks 4–9).** Registry dual index, bridge throwaway lifecycle, DTMF gate, webhook handler updates. End-to-end working.
- **PR3 — Integration tests (Task 10).** Scenario coverage. Can land with PR2 if convenient.

---

## File Structure

| File | Status | Owner Task |
|---|---|---|
| `src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs` | modified | 1 |
| `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs` | modified | 1 |
| `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs` | modified | 2 |
| `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokVoiceSession.cs` | modified | 3 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs` | modified | 4 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxBridgeRegistry.cs` | modified | 5 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs` | modified | 6, 7, 8 |
| `src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs` | modified | 9 |
| `src/agent/OpenAgent.Tests/Channels/TelnyxDtmfExtensionTests.cs` | new | 10 |

---

## Task 1: `IVoiceSession.RebindConversationAsync` contract + Gemini no-op

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs`
- Modify: `src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs`

- [ ] **Step 1: Add the method to `IVoiceSession`**

In `ILlmVoiceProvider.cs`, add to the `IVoiceSession` interface (next to `RefreshSystemPromptAsync`, `AddUserMessageAsync`, `RequestResponseAsync`):

```csharp
/// <summary>
/// Replace the conversation backing this session in flight: refresh the system prompt
/// from the new conversation's skills/intention/summary, and inject its prior messages
/// as <c>conversation.item.create</c> events so the model has its history. Throws
/// <see cref="NotSupportedException"/> on providers whose protocol cannot mutate the
/// system instruction mid-session (notably Gemini Live).
/// </summary>
Task RebindConversationAsync(Conversation newConversation, CancellationToken ct = default);
```

- [ ] **Step 2: Gemini throws**

In `GeminiLiveVoiceSession.cs`, add:

```csharp
public Task RebindConversationAsync(Conversation newConversation, CancellationToken ct = default)
    => throw new NotSupportedException("Gemini Live cannot mutate system instructions mid-session.");
```

- [ ] **Step 3: Build to confirm compile fails for Azure / Grok (proves they need the next tasks)**

Run: `cd src/agent && dotnet build`
Expected: errors in `AzureOpenAiVoiceSession` and `GrokVoiceSession` (do not implement `RebindConversationAsync`).

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Contracts/ILlmVoiceProvider.cs src/agent/OpenAgent.LlmVoice.GeminiLive/GeminiLiveVoiceSession.cs
git commit -m "feat(voice): IVoiceSession.RebindConversationAsync contract + Gemini no-op"
```

---

## Task 2: OpenAI Realtime — `RebindConversationAsync`

**Files:**
- Modify: `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs`

The implementation re-uses the existing `BuildSessionConfig` (live re-fetch + summary append) for the system prompt, then injects history via `conversation.item.create` for each non-tool message in chronological order.

- [ ] **Step 1: Add the method**

```csharp
public async Task RebindConversationAsync(Conversation newConversation, CancellationToken ct = default)
{
    // Swap the bound conversation reference so subsequent persists land on it
    _conversation = newConversation;

    // Push the new system prompt
    await RefreshSystemPromptAsync(ct);

    // Inject prior messages as conversation items so the model has the history
    var stored = _agentLogic.GetMessages(newConversation.Id, includeToolResultBlobs: false);
    foreach (var message in stored)
    {
        if (message.Role is "tool" || string.IsNullOrEmpty(message.Content))
            continue;
        if (message.ToolCalls is not null && message.ToolCalls.Count > 0)
            continue; // skip orphan tool-call assistant messages
        await SendConversationItemAsync(message.Role, message.Content, ct);
    }

    _logger.LogInformation(
        "Rebound voice session to conversation {ConversationId} ({MessageCount} messages injected)",
        newConversation.Id, stored.Count);
}
```

- [ ] **Step 2: Add the `SendConversationItemAsync` helper**

If a similar helper does not already exist, add (private):

```csharp
private async Task SendConversationItemAsync(string role, string text, CancellationToken ct)
{
    var item = new
    {
        type = "conversation.item.create",
        item = new
        {
            type = "message",
            role = role == "assistant" ? "assistant" : "user",
            content = new[]
            {
                new { type = role == "assistant" ? "text" : "input_text", text }
            }
        }
    };
    var json = JsonSerializer.Serialize(item);
    await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
}
```

(Adapt to whatever the existing `AddUserMessageAsync` uses for socket sends — probably the same `_ws.SendAsync` shape.)

- [ ] **Step 3: Build**

Run: `cd src/agent && dotnet build`
Expected: Azure builds; Grok still fails.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs
git commit -m "feat(voice): OpenAI Realtime RebindConversationAsync"
```

---

## Task 3: Grok Realtime — `RebindConversationAsync`

**Files:**
- Modify: `src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokVoiceSession.cs`

Grok mirrors OpenAI's protocol. Implementation is identical in shape — copy from `AzureOpenAiVoiceSession`, adapt to whatever local helpers Grok uses for socket writes.

- [ ] **Step 1: Add the method (same shape as Task 2)**

- [ ] **Step 2: Build all**

Run: `cd src/agent && dotnet build`
Expected: clean build, all three providers compile.

- [ ] **Step 3: Run existing tests**

Run: `cd src/agent && dotnet test`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.LlmVoice.GrokRealtime/GrokVoiceSession.cs
git commit -m "feat(voice): Grok Realtime RebindConversationAsync"
```

---

## Task 4: `PendingBridge` DTMF queue + provider lookup

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxChannelProvider.cs`

The `PendingBridge` record currently carries `(CallControlId, ConversationId, VoiceProviderKey, Cts)`. Conversation creation moves into the bridge, so we drop `ConversationId`. Add `From` (caller number) so the bridge can build the throwaway's `channelChatId`. Add a `ConcurrentQueue<string>` for DTMF that arrives before WS connects.

- [ ] **Step 1: Update `PendingBridge`**

Replace the record at the bottom of `TelnyxChannelProvider.cs`:

```csharp
public sealed record PendingBridge(
    string CallControlId,
    string CallSessionId,
    string From,
    string VoiceProviderKey,
    CancellationTokenSource Cts)
{
    public ConcurrentQueue<string> PendingDtmf { get; } = new();
}
```

- [ ] **Step 2: Add `TryGetPending` to the provider**

```csharp
public bool TryGetPending(string callControlId, out PendingBridge? pending)
{
    var ok = _pending.TryGetValue(callControlId, out var p);
    pending = p;
    return ok;
}
```

(Read-only, distinct from `TryDequeuePending`. Used by the DTMF webhook handler to enqueue the digit without removing the pending entry.)

- [ ] **Step 3: Build (will break webhook handler that constructs `PendingBridge`)**

Run: `cd src/agent && dotnet build`
Expected: error in `TelnyxWebhookEndpoints.OnCallInitiated` — fix in Task 9.

- [ ] **Step 4: Commit (intentionally broken — fixed in Task 9)**

Skip commit; bundle with later tasks.

---

## Task 5: `TelnyxBridgeRegistry` — dual index

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxBridgeRegistry.cs`

Today's registry is keyed only on `conversationId` (used by `EndCallTool`). The DTMF webhook path needs to find the bridge by `callControlId`. The swap also needs to re-key the conversation index when the bridge moves from throwaway to extension. Maintain both indices.

- [ ] **Step 1: Replace the registry**

```csharp
using System.Collections.Concurrent;

namespace OpenAgent.Channel.Telnyx;

public sealed class TelnyxBridgeRegistry
{
    private readonly ConcurrentDictionary<string, object> _byConversation = new();
    private readonly ConcurrentDictionary<string, object> _byCallControl = new();

    public void Register(string callControlId, string conversationId, object bridge)
    {
        _byCallControl[callControlId] = bridge;
        _byConversation[conversationId] = bridge;
    }

    public void Unregister(string callControlId, string conversationId)
    {
        _byCallControl.TryRemove(callControlId, out _);
        _byConversation.TryRemove(conversationId, out _);
    }

    public void UpdateConversationId(string callControlId, string oldConversationId, string newConversationId)
    {
        if (_byCallControl.TryGetValue(callControlId, out var bridge))
        {
            _byConversation.TryRemove(oldConversationId, out _);
            _byConversation[newConversationId] = bridge;
        }
    }

    public bool TryGet(string conversationId, out object? bridge)
        => _byConversation.TryGetValue(conversationId, out bridge);

    public bool TryGetByCallControlId(string callControlId, out object? bridge)
        => _byCallControl.TryGetValue(callControlId, out bridge);
}
```

- [ ] **Step 2: Build (will break TelnyxMediaBridge.Register/Unregister callers)**

Run: `cd src/agent && dotnet build`
Expected: errors in `TelnyxMediaBridge` Register/Unregister sites — fix in Task 6.

---

## Task 6: `TelnyxMediaBridge` — defer conversation creation, throwaway lifecycle

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs`

Move conversation creation out of the webhook handler into the bridge. The bridge now owns the entire lifecycle: throwaway creation on start, possible swap to extension on DTMF, registry registration that survives the swap.

- [ ] **Step 1: Constructor — accept `From` and `CallSessionId` from `PendingBridge` instead of `ConversationId`**

The bridge today reads `_pending.ConversationId`. Replace those reads with internal state populated in `RunAsync`:

```csharp
private string _currentConversationId = string.Empty; // set in RunAsync
```

Constructor signature stays similar but no longer expects a pre-resolved conversation id.

- [ ] **Step 2: In `RunAsync`, create the throwaway as the first step**

After the WS handshake and before bringing up the realtime session:

```csharp
var throwawayChatId = $"{_pending.From}:{_pending.CallSessionId}";
var throwaway = _provider.ConversationStore.FindOrCreateChannelConversation(
    channelType: "telnyx",
    connectionId: _provider.ConnectionId,
    channelChatId: throwawayChatId,
    source: "telnyx",
    provider: _provider.AgentConfig.VoiceProvider,
    model: _provider.AgentConfig.VoiceModel);

if (!string.Equals(throwaway.DisplayName, _pending.From, StringComparison.Ordinal))
    _provider.ConversationStore.UpdateDisplayName(throwaway.Id, _pending.From);

_conversation = throwaway;
_currentConversationId = throwaway.Id;
```

- [ ] **Step 3: Update registry registration**

```csharp
_provider.BridgeRegistry.Register(_pending.CallControlId, _currentConversationId, this);
```

In the `finally` block:

```csharp
_provider.BridgeRegistry.Unregister(_pending.CallControlId, _currentConversationId);
```

- [ ] **Step 4: Build**

Run: `cd src/agent && dotnet build`
Expected: cleaner. Webhook handler still broken — Task 9.

---

## Task 7: `TelnyxMediaBridge` — DTMF gate state + processing

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs`

Add the gate state, the channel that delivers DTMF from the webhook handler, and the gate processing loop that runs alongside the existing media loops.

- [ ] **Step 1: Add fields**

```csharp
private readonly SemaphoreSlim _swapLock = new(1, 1);
private readonly Channel<string> _dtmfChannel = Channel.CreateUnbounded<string>(
    new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
private static readonly TimeSpan DtmfGateWindow = TimeSpan.FromSeconds(8);
private bool _gateClosed; // set true after first digit OR window expiry
```

- [ ] **Step 2: Add `OnDtmfReceived` (called from webhook handler)**

```csharp
public void OnDtmfReceived(string digit)
{
    if (_gateClosed) return;
    _dtmfChannel.Writer.TryWrite(digit);
}
```

- [ ] **Step 3: Drain pre-WS DTMF queue at the top of `RunAsync` (after throwaway is created)**

```csharp
while (_pending.PendingDtmf.TryDequeue(out var digit))
    _dtmfChannel.Writer.TryWrite(digit);
```

- [ ] **Step 4: Start the gate loop alongside the existing audio loops**

In `RunAsync`, after the realtime session is up and the greeting trigger has been sent, kick off:

```csharp
var gateTask = RunDtmfGateAsync(_cts.Token);
```

Add the loop:

```csharp
private async Task RunDtmfGateAsync(CancellationToken ct)
{
    using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    gateCts.CancelAfter(DtmfGateWindow);
    try
    {
        var digit = await _dtmfChannel.Reader.ReadAsync(gateCts.Token);
        _gateClosed = true;
        await PerformDtmfSwapAsync(digit, ct);
    }
    catch (OperationCanceledException) when (gateCts.IsCancellationRequested && !ct.IsCancellationRequested)
    {
        // Window expired with no digit — throwaway continues as the conversation
        _gateClosed = true;
        _logger.LogInformation("DTMF gate closed (no digit) for {CallControlId}", _pending.CallControlId);
    }
}
```

- [ ] **Step 5: Build**

Run: `cd src/agent && dotnet build`
Expected: still has webhook handler errors only.

---

## Task 8: `TelnyxMediaBridge` — swap orchestration + persistence lock

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs`

The actual swap: lookup extension, acquire lock, rebind session, update registry, delete throwaway. Persistence sites that write to `_conversation` need to hold the same lock.

- [ ] **Step 1: Add `PerformDtmfSwapAsync`**

```csharp
private async Task PerformDtmfSwapAsync(string digit, CancellationToken ct)
{
    var extensionChatId = $"{_pending.From},{digit}";
    var extension = _provider.ConversationStore.FindOrCreateChannelConversation(
        channelType: "telnyx",
        connectionId: _provider.ConnectionId,
        channelChatId: extensionChatId,
        source: "telnyx",
        provider: _provider.AgentConfig.VoiceProvider,
        model: _provider.AgentConfig.VoiceModel);

    var desiredDisplayName = $"{_pending.From} ext.{digit}";
    if (!string.Equals(extension.DisplayName, desiredDisplayName, StringComparison.Ordinal))
        _provider.ConversationStore.UpdateDisplayName(extension.Id, desiredDisplayName);

    var throwawayId = _currentConversationId;

    await _swapLock.WaitAsync(ct);
    try
    {
        try
        {
            await _session.RebindConversationAsync(extension, ct);
        }
        catch (NotSupportedException)
        {
            _logger.LogWarning(
                "Voice provider {Provider} does not support mid-session rebind; ignoring DTMF for {CallControlId}",
                _pending.VoiceProviderKey, _pending.CallControlId);
            return;
        }

        _conversation = extension;
        _currentConversationId = extension.Id;
        _provider.BridgeRegistry.UpdateConversationId(_pending.CallControlId, throwawayId, extension.Id);
    }
    finally
    {
        _swapLock.Release();
    }

    // Delete throwaway AFTER the lock is released, so any in-flight persists that
    // already won the lock for the throwaway have completed (and their messages
    // are lost — accepted in the design).
    try { _provider.ConversationStore.Delete(throwawayId); }
    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete throwaway {ConversationId}", throwawayId); }

    _logger.LogInformation(
        "DTMF swap complete: {Throwaway} -> {Extension} (digit {Digit}) for {CallControlId}",
        throwawayId, extension.Id, digit, _pending.CallControlId);
}
```

- [ ] **Step 2: Wrap message persistence with `_swapLock`**

Find the sites in `TelnyxMediaBridge.cs` where the bridge writes assistant or user messages to `_conversation` (typically inside realtime event handlers — `response.done`, `conversation.item.input_audio_transcription.completed`, etc.). Wrap each persist with:

```csharp
await _swapLock.WaitAsync(ct);
try
{
    // existing AddMessage / persist logic
}
finally
{
    _swapLock.Release();
}
```

(Keep critical sections short — only the persist call, not the full handler.)

- [ ] **Step 3: Build + run tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: tests still pass (except webhook test — Task 9).

---

## Task 9: `TelnyxWebhookEndpoints` — DTMF case + drop conversation lookup

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxWebhookEndpoints.cs`

Two changes: stop creating the conversation in `OnCallInitiated` (the bridge does it now), and add a `call.dtmf.received` case that dispatches to bridge or pending entry.

- [ ] **Step 1: Update `OnCallInitiated` to skip conversation creation**

Remove the `FindOrCreateChannelConversation` and `UpdateDisplayName` calls. Update the `PendingBridge` constructor call to match the new shape (no `ConversationId`, add `CallSessionId` and `From`):

```csharp
var p = env.Data!.Payload!;
var from = p.From ?? "";
var callControlId = p.CallControlId ?? "";
var callSessionId = p.CallSessionId ?? Guid.NewGuid().ToString("N"); // fallback for safety

// Allowlist check (unchanged)
if (provider.Options.AllowedNumbers.Count > 0 && !provider.Options.AllowedNumbers.Contains(from))
{
    await provider.CallControlClient.HangupAsync(callControlId, ct);
    return Results.Ok();
}

await provider.CallControlClient.AnswerAsync(callControlId, ct);

var cts = new CancellationTokenSource();
var pending = new PendingBridge(
    CallControlId: callControlId,
    CallSessionId: callSessionId,
    From: from,
    VoiceProviderKey: provider.AgentConfig.VoiceProvider,
    Cts: cts);

if (!provider.TryRegisterPending(callControlId, pending))
    return Results.Ok();

// (rest unchanged: 30s self-evict, streaming start, etc.)
```

Note: `TelnyxPayload` already has `call_control_id`, `connection_id`, `from`, `to`. Add `call_session_id`:

```csharp
[JsonPropertyName("call_session_id")] public string? CallSessionId { get; set; }
```

- [ ] **Step 2: Add the DTMF case**

In the switch:

```csharp
"call.dtmf.received" => OnDtmfReceived(provider, env),
```

Add the handler:

```csharp
private static IResult OnDtmfReceived(TelnyxChannelProvider provider, TelnyxWebhookEnvelope env)
{
    var callControlId = env.Data!.Payload!.CallControlId ?? "";
    var digit = env.Data!.Payload!.Digit ?? "";
    if (string.IsNullOrEmpty(digit) || string.IsNullOrEmpty(callControlId))
        return Results.Ok();

    // Active bridge first — most calls are past the WS handshake by the time DTMF lands
    if (provider.BridgeRegistry.TryGetByCallControlId(callControlId, out var bridge)
        && bridge is ITelnyxBridge typed)
    {
        typed.OnDtmfReceived(digit);
        return Results.Ok();
    }

    // Pre-WS path: enqueue on pending entry, bridge drains on start
    if (provider.TryGetPending(callControlId, out var pending) && pending is not null)
    {
        pending.PendingDtmf.Enqueue(digit);
    }

    return Results.Ok();
}
```

Add `Digit` to `TelnyxPayload`:

```csharp
[JsonPropertyName("digit")] public string? Digit { get; set; }
```

Add `OnDtmfReceived` to the `ITelnyxBridge` interface (find it in the same project — currently has `SetPendingHangup`):

```csharp
public interface ITelnyxBridge
{
    void SetPendingHangup();
    void OnDtmfReceived(string digit);
}
```

- [ ] **Step 3: Build + run tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: clean build, all green.

- [ ] **Step 4: Commit (PR2 — the whole Telnyx wiring chain)**

```bash
git add src/agent/OpenAgent.Channel.Telnyx
git commit -m "feat(telnyx): DTMF extension routing with throwaway/extension swap"
```

---

## Task 10: Integration tests

**Files:**
- Create: `src/agent/OpenAgent.Tests/Channels/TelnyxDtmfExtensionTests.cs`

Cover the four scenarios that matter end-to-end. Use `WebApplicationFactory<Program>` for the host, mock the realtime provider with a fake `IVoiceSession` that records calls.

- [ ] **Step 1: Test — DTMF arrives mid-window, swap happens**

Setup: in-memory `TelnyxChannelProvider`, fake realtime provider whose `IVoiceSession.RebindConversationAsync` records the new conversation id. Simulate `call.initiated` → `streaming.started` → bridge starts → fire `call.dtmf.received` with digit `1` after 100 ms.

Assert:
- Throwaway conversation deleted from store
- Extension conversation `{from},1` exists with `DisplayName = "{from} ext.1"`
- Fake session received exactly one `RebindConversationAsync` call with the extension
- Registry's `TryGet(extension.Id, ...)` returns the bridge; old throwaway id no longer registered

- [ ] **Step 2: Test — DTMF arrives BEFORE WS connects (pre-WS path)**

Simulate: webhook for `call.initiated` → webhook for `call.dtmf.received` BEFORE the streaming WS opens → THEN the streaming WS opens.

Assert:
- Bridge drains the pre-WS digit on start
- Swap happens within ~200 ms of WS connect
- Registry has extension conversation registered

- [ ] **Step 3: Test — gate timeout (no DTMF) leaves throwaway as the conversation**

Simulate: bridge starts, no DTMF arrives, wait > 8 seconds, then hang up.

Assert:
- Throwaway conversation still exists
- No `RebindConversationAsync` call was made
- No extension conversation was created

- [ ] **Step 4: Test — Gemini provider degrades gracefully**

Setup: fake `IVoiceSession` whose `RebindConversationAsync` throws `NotSupportedException`. Simulate DTMF mid-window.

Assert:
- Bridge does not crash
- Throwaway conversation remains intact (NOT deleted)
- Extension conversation is not registered as the active one
- Warning log emitted

- [ ] **Step 5: Test — second DTMF in window is ignored (single-shot)**

Simulate: digit `1` at 100 ms, digit `2` at 200 ms.

Assert:
- Exactly one `RebindConversationAsync` call (with extension `{from},1`)
- No `{from},2` conversation created

- [ ] **Step 6: Run tests**

Run: `cd src/agent && dotnet test --filter "FullyQualifiedName~TelnyxDtmfExtensionTests"`
Expected: all 5 tests green.

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent.Tests/Channels/TelnyxDtmfExtensionTests.cs
git commit -m "test(telnyx): DTMF extension routing scenarios"
```

---

## Acceptance

- A test call to `+4535150636,1` lands in conversation `{from},1` (visible in UI as `+4551504261 ext.1`)
- A test call to `+4535150636` (no extension) lands in a fresh per-call conversation, throwaway remains
- Repeat extension calls reuse the same `{from},N` thread (history continuity for extensions)
- Multiple concurrent calls from the same number work independently
- Logs show clear `DTMF swap complete` or `DTMF gate closed (no digit)` for every call
- All existing Telnyx tests still pass
