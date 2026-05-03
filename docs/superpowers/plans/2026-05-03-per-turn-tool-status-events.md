# Per-Turn Tool Status Events Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Emit one `tool_call_started` event when the LLM's first tool-call round begins and one `tool_call_completed` event when all tool rounds finish, per user turn — so clients (WebSocket text, WebSocket voice, REST) can bracket "agent is working" UX without tracking individual tool calls.

**Architecture:** New `ToolCallStarted` / `ToolCallCompleted` subtypes on `CompletionEvent`. Text providers yield them around the tool-call `for` loop. Voice sessions aggregate the existing per-tool `VoiceToolCallStarted`/`VoiceToolCallCompleted` into per-turn events at the endpoint level (voice sessions keep emitting per-tool for internal use, endpoints aggregate). Telnyx thinking pump disabled for now — re-enabled in a follow-up using the same per-turn events.

**Tech Stack:** C#/.NET, xUnit + WebApplicationFactory

---

### Task 1: Add ToolCallStarted / ToolCallCompleted to CompletionEvent

**Files:**
- Modify: `src/agent/OpenAgent.Models/Common/CompletionEvent.cs`

- [ ] **Step 1: Add the two new record types**

```csharp
// Append after AssistantMessageSaved in CompletionEvent.cs:

/// <summary>
/// Emitted once per user turn when the LLM's response requires tool execution.
/// Signals that one or more tool-call rounds are about to begin.
/// </summary>
public sealed record ToolCallStarted : CompletionEvent;

/// <summary>
/// Emitted once per user turn after all tool-call rounds have completed and
/// the LLM is producing its final text response.
/// </summary>
public sealed record ToolCallCompleted : CompletionEvent;
```

- [ ] **Step 2: Build to verify compilation**

Run: `cd src/agent && dotnet build OpenAgent.Models`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.Models/Common/CompletionEvent.cs
git commit -m "feat(models): add ToolCallStarted/ToolCallCompleted CompletionEvent subtypes"
```

---

### Task 2: Add WebSocket text contract for status events

**Files:**
- Modify: `src/agent/OpenAgent.Models/Text/TextWebSocketContracts.cs`

- [ ] **Step 1: Add TextWebSocketToolCallStarted and TextWebSocketToolCallCompleted**

Append after `TextWebSocketDone` in `TextWebSocketContracts.cs`:

```csharp
/// <summary>
/// Outbound text WebSocket status payload — tool execution is starting for this turn.
/// </summary>
public sealed class TextWebSocketToolCallStarted
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_call_started";
}

/// <summary>
/// Outbound text WebSocket status payload — all tool execution is done for this turn.
/// </summary>
public sealed class TextWebSocketToolCallCompleted
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_call_completed";
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `cd src/agent && dotnet build OpenAgent.Models`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.Models/Text/TextWebSocketContracts.cs
git commit -m "feat(models): add TextWebSocket tool status contracts"
```

---

### Task 3: Emit ToolCallStarted / ToolCallCompleted in AzureOpenAiTextProvider

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs:84-327` (the `CompleteAsync` with conversation overload)

The tool-call loop is `for (var round = 0; round < maxToolRounds; round++)` at line 122. The first time `finishReason == "tool_calls"` (line 227), yield `ToolCallStarted`. When the loop exits normally (line 321 `yield break`), yield `ToolCallCompleted` before the final response events.

- [ ] **Step 1: Add a `toolCallsStarted` flag and emit events**

Add a `bool toolCallsStarted = false;` before the `for` loop (after line 121). Inside the `if (finishReason == "tool_calls" ...)` block (line 227), before the existing code, add:

```csharp
if (!toolCallsStarted)
{
    toolCallsStarted = true;
    yield return new ToolCallStarted();
}
```

After the `for` loop's final text-response section, before `yield return new AssistantMessageSaved(assistantMessageId)` at line 320, add:

```csharp
if (toolCallsStarted)
    yield return new ToolCallCompleted();
```

- [ ] **Step 2: Build to verify compilation**

Run: `cd src/agent && dotnet build OpenAgent.LlmText.OpenAIAzure`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs
git commit -m "feat(azure-text): emit per-turn ToolCallStarted/Completed around tool loop"
```

---

### Task 4: Emit ToolCallStarted / ToolCallCompleted in AnthropicSubscriptionTextProvider

**Files:**
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs:111-394` (the `CompleteAsync` with conversation overload)

Same pattern as Task 3. The tool-call loop is `for (var round = 0; round < maxToolRounds; round++)` at line 144. The `if (stopReason == "tool_use" ...)` block is at line 281.

- [ ] **Step 1: Add a `toolCallsStarted` flag and emit events**

Add a `bool toolCallsStarted = false;` before the `for` loop (after line 143). Inside the `if (stopReason == "tool_use" ...)` block (line 281), before the existing code, add:

```csharp
if (!toolCallsStarted)
{
    toolCallsStarted = true;
    yield return new ToolCallStarted();
}
```

Before `yield return new AssistantMessageSaved(assistantMessageId)` at line 387, add:

```csharp
if (toolCallsStarted)
    yield return new ToolCallCompleted();
```

- [ ] **Step 2: Build to verify compilation**

Run: `cd src/agent && dotnet build OpenAgent.LlmText.AnthropicSubscription`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs
git commit -m "feat(anthropic-text): emit per-turn ToolCallStarted/Completed around tool loop"
```

---

### Task 5: Map new events in WebSocketTextEndpoints

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebSocketTextEndpoints.cs:108-131` (the switch in `RunChatLoopAsync`)

- [ ] **Step 1: Add cases for the new event types**

Add two new cases inside the `switch (evt)` block at line 110, after the `ToolResultEvent` case:

```csharp
case ToolCallStarted:
    await SendJsonAsync(ws, new TextWebSocketToolCallStarted(), ct);
    break;
case ToolCallCompleted:
    await SendJsonAsync(ws, new TextWebSocketToolCallCompleted(), ct);
    break;
```

Also add the `using OpenAgent.Models.Text;` import is already present (line 11). No new using needed — `ToolCallStarted`/`ToolCallCompleted` are in `OpenAgent.Models.Common` which is already imported (line 8).

- [ ] **Step 2: Build to verify compilation**

Run: `cd src/agent && dotnet build OpenAgent.Api`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/WebSocketTextEndpoints.cs
git commit -m "feat(ws-text): forward ToolCallStarted/Completed as JSON events"
```

---

### Task 6: Map new events in ChatEndpoints (REST)

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs:60-69` (the event collection switch)

- [ ] **Step 1: Add cases for the new event types**

In the `events.Add(evt switch { ... })` expression at line 62, add cases before the `_ =>` fallback:

```csharp
ToolCallStarted => new { type = "tool_call_started" },
ToolCallCompleted => new { type = "tool_call_completed" },
```

- [ ] **Step 2: Build to verify compilation**

Run: `cd src/agent && dotnet build OpenAgent.Api`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/ChatEndpoints.cs
git commit -m "feat(rest-chat): include ToolCallStarted/Completed in JSON response array"
```

---

### Task 7: Aggregate per-tool voice events into per-turn in WebSocketVoiceEndpoints

**Files:**
- Modify: `src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs:119-189` (the `WriteLoopAsync` switch)

The voice sessions emit `VoiceToolCallStarted` per tool and `VoiceToolCallCompleted` per tool. We want to aggregate to per-turn: send `thinking_started` on the first `VoiceToolCallStarted`, send `thinking_stopped` when the active count drops to zero.

- [ ] **Step 1: Replace the direct mapping with ref-counted aggregation**

In `WriteLoopAsync`, add a counter before the `await foreach`:

```csharp
var activeToolCalls = 0;
```

Replace the two existing cases:

```csharp
// Old:
case VoiceToolCallStarted:
    await SendJsonAsync(ws, new VoiceThinkingStartedEvent(), ct);
    break;

case VoiceToolCallCompleted:
    await SendJsonAsync(ws, new VoiceThinkingStoppedEvent(), ct);
    break;
```

With:

```csharp
case VoiceToolCallStarted:
    if (activeToolCalls++ == 0)
        await SendJsonAsync(ws, new VoiceThinkingStartedEvent(), ct);
    break;
case VoiceToolCallCompleted:
    if (activeToolCalls > 0 && --activeToolCalls == 0)
        await SendJsonAsync(ws, new VoiceThinkingStoppedEvent(), ct);
    break;
```

This sends exactly one `thinking_started` when the first tool fires, and one `thinking_stopped` when the last completes — per-turn behavior matching the text side.

- [ ] **Step 2: Build to verify compilation**

Run: `cd src/agent && dotnet build OpenAgent.Api`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.Api/Endpoints/WebSocketVoiceEndpoints.cs
git commit -m "feat(ws-voice): aggregate per-tool voice events into per-turn thinking_started/stopped"
```

---

### Task 8: Disable Telnyx thinking pump

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs:256-268` (the `VoiceToolCallStarted`/`VoiceToolCallCompleted` cases in `WriteLoopAsync`)

- [ ] **Step 1: Comment out the pump activation in WriteLoopAsync**

Replace the two cases:

```csharp
case VoiceToolCallStarted:
    if (_activeToolCalls++ == 0)
        StartPump();
    break;
case VoiceToolCallCompleted:
    if (_activeToolCalls > 0 && --_activeToolCalls == 0)
        StopPump();
    break;
```

With:

```csharp
// Thinking pump disabled — re-enable once per-turn status events land on Telnyx.
case VoiceToolCallStarted:
case VoiceToolCallCompleted:
    break;
```

- [ ] **Step 2: Build to verify compilation**

Run: `cd src/agent && dotnet build OpenAgent.Channel.Telnyx`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```
git add src/agent/OpenAgent.Channel.Telnyx/TelnyxMediaBridge.cs
git commit -m "chore(telnyx): disable thinking pump pending per-turn status event integration"
```

---

### Task 9: Add integration test — REST chat emits ToolCallStarted/Completed

**Files:**
- Modify: `src/agent/OpenAgent.Tests/ChatEndpointTests.cs`

- [ ] **Step 1: Add a FakeToolCallingTextProvider inner class**

Add inside `ChatEndpointTests`, after the existing `FakeTextProvider`:

```csharp
private sealed class FakeToolCallingTextProvider : ILlmTextProvider
{
    public string Key => "text-provider";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }
    public int? GetContextWindow(string model) => null;

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(Conversation conversation, Message userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ToolCallStarted();
        yield return new ToolCallEvent("tc1", "search_web", "{}");
        yield return new ToolResultEvent("tc1", "search_web", "result1");
        yield return new ToolCallCompleted();
        yield return new TextDelta("done");
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(IReadOnlyList<Message> messages, string model,
        CompletionOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextDelta("raw");
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Add the test method**

```csharp
[Fact]
public async Task SendMessage_WithToolCalls_EmitsToolCallStartedAndCompleted()
{
    var factory = _factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(ILlmTextProvider));
            var fake = new FakeToolCallingTextProvider();
            services.AddKeyedSingleton<ILlmTextProvider>("azure-openai-text", fake);
            services.AddSingleton<ILlmTextProvider>(fake);
        });
    });

    var client = factory.CreateClient();
    client.DefaultRequestHeaders.Add("X-Api-Key", "dev-api-key-change-me");
    var conversationId = Guid.NewGuid().ToString();

    var response = await client.PostAsJsonAsync(
        $"/api/conversations/{conversationId}/messages",
        new { Content = "search something" });

    response.EnsureSuccessStatusCode();

    var events = await response.Content.ReadFromJsonAsync<JsonElement[]>();
    Assert.NotNull(events);
    Assert.Equal(5, events.Length);
    Assert.Equal("tool_call_started", events[0].GetProperty("type").GetString());
    Assert.Equal("tool_call", events[1].GetProperty("type").GetString());
    Assert.Equal("tool_result", events[2].GetProperty("type").GetString());
    Assert.Equal("tool_call_completed", events[3].GetProperty("type").GetString());
    Assert.Equal("text", events[4].GetProperty("type").GetString());
}
```

- [ ] **Step 3: Run the test to verify it passes**

Run: `cd src/agent && dotnet test --filter "SendMessage_WithToolCalls_EmitsToolCallStartedAndCompleted" -v minimal`
Expected: PASS

- [ ] **Step 4: Commit**

```
git add src/agent/OpenAgent.Tests/ChatEndpointTests.cs
git commit -m "test(chat): verify REST response includes per-turn ToolCallStarted/Completed"
```

---

### Task 10: Run full test suite and push

- [ ] **Step 1: Run all tests**

Run: `cd src/agent && dotnet test -v minimal`
Expected: All tests pass.

- [ ] **Step 2: Push**

```
git push
```
