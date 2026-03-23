# WhatsApp Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add WhatsApp as a channel provider using Baileys (Node.js) as a managed child process, and fix conversation mapping so each platform chat gets its own conversation.

**Architecture:** A .NET `WhatsAppChannelProvider` spawns a Node.js child process running Baileys. Communication uses stdin/stdout JSON lines. The Node process handles the WhatsApp Web protocol; all business logic stays in .NET. Conversation IDs are derived per-chat for both Telegram and WhatsApp.

**Tech Stack:** .NET 10, ASP.NET Core, Node.js, @whiskeysockets/baileys, System.Text.Json, xUnit

**Spec:** `docs/plans/2026-03-23-whatsapp-channel-design.md`

---

## File Map

### New project: `src/agent/OpenAgent.Channel.WhatsApp/`

| File | Responsibility |
|------|---------------|
| `OpenAgent.Channel.WhatsApp.csproj` | Project file — references Contracts, Models; includes `node/` as content |
| `WhatsAppOptions.cs` | Config model (AllowedChatIds) |
| `WhatsAppAccessControl.cs` | JID allowlist check |
| `WhatsAppNodeProcess.cs` | Spawns/manages Node child process, stdin/stdout JSON line protocol, ping/pong, reconnect |
| `WhatsAppChannelProvider.cs` | IChannelProvider — lifecycle (start/stop), QR state machine, dedup cache |
| `WhatsAppChannelProviderFactory.cs` | IChannelProviderFactory — deserializes config, creates provider |
| `WhatsAppMessageHandler.cs` | Inbound message processing, LLM call, composing indicator, response send |
| `WhatsAppMarkdownConverter.cs` | Markdown to WhatsApp formatting, chunking |
| `WhatsAppEndpoints.cs` | QR code endpoint |
| `node/package.json` | Baileys dependency (pinned version) |
| `node/baileys-bridge.js` | Node script — Baileys socket, JSON line protocol |

### New test files: `src/agent/OpenAgent.Tests/`

| File | Responsibility |
|------|---------------|
| `WhatsAppAccessControlTests.cs` | AllowedChatIds tests |
| `WhatsAppMarkdownConverterTests.cs` | Formatting conversion + chunking tests |
| `WhatsAppNodeProcessTests.cs` | JSON line parsing, stdin serialization tests |
| `WhatsAppMessageHandlerTests.cs` | Inbound flow, access control, LLM call, dedup, sender attribution |

### Modified files

| File | Change |
|------|--------|
| `src/agent/OpenAgent/OpenAgent.csproj` | Add ProjectReference to OpenAgent.Channel.WhatsApp |
| `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj` | Add ProjectReference to OpenAgent.Channel.WhatsApp |
| `src/agent/OpenAgent/Program.cs` | Register WhatsAppChannelProviderFactory, map WhatsApp endpoints |
| `src/agent/OpenAgent.sln` | Add WhatsApp project |
| `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs` | Derive conversation ID from chat.Id instead of connection's ConversationId |
| `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs` | Update expected conversation IDs |
| `Dockerfile` | Add Node.js to runtime, install Baileys deps |

---

## Task 1: Project Scaffolding

**Files:**
- Create: `src/agent/OpenAgent.Channel.WhatsApp/OpenAgent.Channel.WhatsApp.csproj`
- Modify: `src/agent/OpenAgent.sln`
- Modify: `src/agent/OpenAgent/OpenAgent.csproj`
- Modify: `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj`

- [ ] **Step 1: Create the project directory**

```bash
cd src/agent && mkdir -p OpenAgent.Channel.WhatsApp
```

- [ ] **Step 2: Create the csproj file**

Create `src/agent/OpenAgent.Channel.WhatsApp/OpenAgent.Channel.WhatsApp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Markdig" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
    <ProjectReference Include="..\OpenAgent.Models\OpenAgent.Models.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="node\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add project to solution and host**

```bash
cd src/agent && dotnet sln add OpenAgent.Channel.WhatsApp/OpenAgent.Channel.WhatsApp.csproj
```

Add to `src/agent/OpenAgent/OpenAgent.csproj`:

```xml
<ProjectReference Include="..\OpenAgent.Channel.WhatsApp\OpenAgent.Channel.WhatsApp.csproj" />
```

Add to `src/agent/OpenAgent.Tests/OpenAgent.Tests.csproj`:

```xml
<ProjectReference Include="..\OpenAgent.Channel.WhatsApp\OpenAgent.Channel.WhatsApp.csproj" />
```

- [ ] **Step 4: Verify build**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds with no errors.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/OpenAgent.Channel.WhatsApp.csproj src/agent/OpenAgent.sln src/agent/OpenAgent/OpenAgent.csproj
git commit -m "chore: scaffold OpenAgent.Channel.WhatsApp project"
```

---

## Task 2: WhatsAppOptions and WhatsAppAccessControl

**Files:**
- Create: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppOptions.cs`
- Create: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppAccessControl.cs`
- Create: `src/agent/OpenAgent.Tests/WhatsAppAccessControlTests.cs`

- [ ] **Step 1: Write access control tests**

Create `src/agent/OpenAgent.Tests/WhatsAppAccessControlTests.cs`:

```csharp
using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests;

public class WhatsAppAccessControlTests
{
    [Fact]
    public void IsAllowed_EmptyList_BlocksEveryone()
    {
        var ac = new WhatsAppAccessControl([]);
        Assert.False(ac.IsAllowed("+4512345678@s.whatsapp.net"));
    }

    [Fact]
    public void IsAllowed_MatchingJid_ReturnsTrue()
    {
        var ac = new WhatsAppAccessControl(["+4512345678@s.whatsapp.net"]);
        Assert.True(ac.IsAllowed("+4512345678@s.whatsapp.net"));
    }

    [Fact]
    public void IsAllowed_NonMatchingJid_ReturnsFalse()
    {
        var ac = new WhatsAppAccessControl(["+4512345678@s.whatsapp.net"]);
        Assert.False(ac.IsAllowed("+4599999999@s.whatsapp.net"));
    }

    [Fact]
    public void IsAllowed_GroupJid_ReturnsTrue()
    {
        var ac = new WhatsAppAccessControl(["120363001234567890@g.us"]);
        Assert.True(ac.IsAllowed("120363001234567890@g.us"));
    }

    [Fact]
    public void IsAllowed_MixedList_MatchesBoth()
    {
        var ac = new WhatsAppAccessControl(["+4512345678@s.whatsapp.net", "120363001234567890@g.us"]);
        Assert.True(ac.IsAllowed("+4512345678@s.whatsapp.net"));
        Assert.True(ac.IsAllowed("120363001234567890@g.us"));
        Assert.False(ac.IsAllowed("+4599999999@s.whatsapp.net"));
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

```bash
cd src/agent && dotnet test --filter "WhatsAppAccessControlTests"
```

Expected: Build error — `WhatsAppAccessControl` does not exist.

- [ ] **Step 3: Implement WhatsAppOptions**

Create `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppOptions.cs`:

```csharp
namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Configuration for the WhatsApp channel, deserialized from a connection's config blob.
/// </summary>
public sealed class WhatsAppOptions
{
    /// <summary>
    /// Chat IDs allowed to interact with the bot.
    /// Accepts JID format: "+4512345678@s.whatsapp.net" for DMs, "120363xxx@g.us" for groups.
    /// Empty or missing = all chats blocked (secure by default).
    /// </summary>
    public List<string> AllowedChatIds { get; set; } = [];
}
```

- [ ] **Step 4: Implement WhatsAppAccessControl**

Create `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppAccessControl.cs`:

```csharp
namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Checks whether a WhatsApp chat JID is in the allowlist.
/// Empty allowlist = all chats blocked (secure by default).
/// </summary>
public sealed class WhatsAppAccessControl
{
    private readonly HashSet<string> _allowedChatIds;

    public WhatsAppAccessControl(IEnumerable<string> allowedChatIds)
    {
        _allowedChatIds = new HashSet<string>(allowedChatIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns true if the chat JID is in the allowlist.</summary>
    public bool IsAllowed(string chatId) => _allowedChatIds.Contains(chatId);
}
```

- [ ] **Step 5: Run tests — verify they pass**

```bash
cd src/agent && dotnet test --filter "WhatsAppAccessControlTests"
```

Expected: All 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppOptions.cs src/agent/OpenAgent.Channel.WhatsApp/WhatsAppAccessControl.cs src/agent/OpenAgent.Tests/WhatsAppAccessControlTests.cs
git commit -m "feat: add WhatsAppOptions and WhatsAppAccessControl"
```

---

## Task 3: WhatsAppMarkdownConverter

**Files:**
- Create: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMarkdownConverter.cs`
- Create: `src/agent/OpenAgent.Tests/WhatsAppMarkdownConverterTests.cs`

- [ ] **Step 1: Write converter tests**

Create `src/agent/OpenAgent.Tests/WhatsAppMarkdownConverterTests.cs`:

```csharp
using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests;

public class WhatsAppMarkdownConverterTests
{
    [Fact]
    public void ToWhatsApp_Bold_ConvertsToBoldSyntax()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Hello **world**");
        Assert.Contains("*world*", result);
    }

    [Fact]
    public void ToWhatsApp_Italic_ConvertsToUnderscoreSyntax()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Hello *italic*");
        Assert.Contains("_italic_", result);
    }

    [Fact]
    public void ToWhatsApp_Strikethrough_ConvertToTildeSyntax()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Hello ~~strike~~");
        Assert.Contains("~strike~", result);
    }

    [Fact]
    public void ToWhatsApp_InlineCode_PassesThrough()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Use `code` here");
        Assert.Contains("`code`", result);
    }

    [Fact]
    public void ToWhatsApp_CodeBlock_PassesThrough()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("```\nvar x = 1;\n```");
        Assert.Contains("```", result);
        Assert.Contains("var x = 1;", result);
    }

    [Fact]
    public void ToWhatsApp_Link_ConvertsToTextAndUrl()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("Visit [Google](https://google.com)");
        Assert.Contains("Google", result);
        Assert.Contains("https://google.com", result);
    }

    [Fact]
    public void ToWhatsApp_Heading_ConvertsToBold()
    {
        var result = WhatsAppMarkdownConverter.ToWhatsApp("# Title");
        Assert.Contains("*Title*", result);
    }

    [Fact]
    public void ChunkText_ShortText_ReturnsSingleChunk()
    {
        var chunks = WhatsAppMarkdownConverter.ChunkText("Hello world", 4096);
        Assert.Single(chunks);
        Assert.Equal("Hello world", chunks[0]);
    }

    [Fact]
    public void ChunkText_LongText_SplitsOnParagraph()
    {
        var text = new string('a', 2000) + "\n\n" + new string('b', 2000);
        var chunks = WhatsAppMarkdownConverter.ChunkText(text, 2500);
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void ChunkText_ExactLimit_DoesNotSplit()
    {
        var text = new string('a', 4096);
        var chunks = WhatsAppMarkdownConverter.ChunkText(text, 4096);
        Assert.Single(chunks);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

```bash
cd src/agent && dotnet test --filter "WhatsAppMarkdownConverterTests"
```

Expected: Build error — `WhatsAppMarkdownConverter` does not exist.

- [ ] **Step 3: Implement WhatsAppMarkdownConverter**

Create `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMarkdownConverter.cs`. Use Markdig for parsing (same dependency as Telegram). The converter walks the AST and emits WhatsApp-compatible formatting:

- `EmphasisInline` with `**` delimiter -> `*text*` (bold)
- `EmphasisInline` with `*` delimiter -> `_text_` (italic)
- `EmphasisInline` with `~~` delimiter -> `~text~` (strikethrough)
- `CodeInline` -> `` `text` ``
- `FencedCodeBlock` -> ` ```\ntext\n``` `
- `LinkInline` -> `text (url)`
- `HeadingBlock` -> `*text*` (bold)
- All other blocks -> render as plain text

`ChunkText(string text, int maxLength)` splits on `\n\n` first, then `\n`, then hard-cuts. Returns `List<string>`.

Reference `TelegramMarkdownConverter.cs` for the Markdig AST walking pattern — adapt the rendering for WhatsApp syntax.

- [ ] **Step 4: Run tests — verify they pass**

```bash
cd src/agent && dotnet test --filter "WhatsAppMarkdownConverterTests"
```

Expected: All 10 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMarkdownConverter.cs src/agent/OpenAgent.Tests/WhatsAppMarkdownConverterTests.cs
git commit -m "feat: add WhatsAppMarkdownConverter with Markdig-based formatting"
```

---

## Task 4: WhatsAppNodeProcess — JSON Line Protocol and Process Management

This is the core bridge between .NET and Node.js. It manages the child process lifecycle, serializes stdin writes through a Channel, and parses stdout JSON lines.

**Files:**
- Create: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs`
- Create: `src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs`

- [ ] **Step 1: Write tests for JSON line parsing and stdin serialization**

Create `src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs`:

```csharp
using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests;

public class WhatsAppNodeProcessTests
{
    [Fact]
    public void ParseLine_QrMessage_ReturnsQrEvent()
    {
        var evt = WhatsAppNodeProcess.ParseLine("{\"type\":\"qr\",\"data\":\"2@AbC123\"}");
        Assert.NotNull(evt);
        Assert.Equal("qr", evt.Type);
        Assert.Equal("2@AbC123", evt.Data);
    }

    [Fact]
    public void ParseLine_ConnectedMessage_ReturnsConnectedEvent()
    {
        var evt = WhatsAppNodeProcess.ParseLine("{\"type\":\"connected\",\"jid\":\"+45@s.whatsapp.net\"}");
        Assert.NotNull(evt);
        Assert.Equal("connected", evt.Type);
    }

    [Fact]
    public void ParseLine_MessageEvent_ParsesAllFields()
    {
        var json = "{\"type\":\"message\",\"id\":\"ABC\",\"chatId\":\"+45@s.whatsapp.net\",\"from\":\"+45\",\"pushName\":\"Alice\",\"text\":\"Hello\",\"timestamp\":1711180800}";
        var evt = WhatsAppNodeProcess.ParseLine(json);
        Assert.NotNull(evt);
        Assert.Equal("message", evt.Type);
        Assert.Equal("ABC", evt.Id);
        Assert.Equal("+45@s.whatsapp.net", evt.ChatId);
        Assert.Equal("+45", evt.From);
        Assert.Equal("Alice", evt.PushName);
        Assert.Equal("Hello", evt.Text);
    }

    [Fact]
    public void ParseLine_DisconnectedEvent_IncludesReason()
    {
        var evt = WhatsAppNodeProcess.ParseLine("{\"type\":\"disconnected\",\"reason\":\"loggedOut\"}");
        Assert.NotNull(evt);
        Assert.Equal("disconnected", evt.Type);
        Assert.Equal("loggedOut", evt.Reason);
    }

    [Fact]
    public void ParseLine_InvalidJson_ReturnsNull()
    {
        var evt = WhatsAppNodeProcess.ParseLine("not json");
        Assert.Null(evt);
    }

    [Fact]
    public void ParseLine_EmptyLine_ReturnsNull()
    {
        var evt = WhatsAppNodeProcess.ParseLine("");
        Assert.Null(evt);
    }

    [Fact]
    public void FormatCommand_Send_ProducesValidJson()
    {
        var json = WhatsAppNodeProcess.FormatSendCommand("+45@s.whatsapp.net", "Hello");
        Assert.Contains("\"type\":\"send\"", json);
        Assert.Contains("\"chatId\":\"+45@s.whatsapp.net\"", json);
        Assert.Contains("\"text\":\"Hello\"", json);
    }

    [Fact]
    public void FormatCommand_Composing_ProducesValidJson()
    {
        var json = WhatsAppNodeProcess.FormatComposingCommand("+45@s.whatsapp.net");
        Assert.Contains("\"type\":\"composing\"", json);
        Assert.Contains("\"chatId\":\"+45@s.whatsapp.net\"", json);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

```bash
cd src/agent && dotnet test --filter "WhatsAppNodeProcessTests"
```

Expected: Build error — `WhatsAppNodeProcess` does not exist.

- [ ] **Step 3: Implement WhatsAppNodeProcess**

Create `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs`. Key responsibilities:

1. **`NodeEvent` record** — parsed from stdout JSON lines. Fields: `Type`, `Data`, `Jid`, `Id`, `ChatId`, `From`, `PushName`, `Text`, `Timestamp`, `Reason`, `Message`.

2. **`ParseLine(string line)`** — static method. Deserializes a JSON line into a `NodeEvent`. Returns null on invalid/empty input. Uses `System.Text.Json` with `PropertyNameCaseInsensitive = true`.

3. **`FormatSendCommand(string chatId, string text)`** — static. Returns JSON string for send command.

4. **`FormatComposingCommand(string chatId)`** — static. Returns JSON string for composing command.

5. **Script path resolution** — The Node script is copied to the output directory via the csproj `Content` item. Resolve the path at runtime relative to `AppContext.BaseDirectory`:
   ```csharp
   var scriptPath = Path.Combine(AppContext.BaseDirectory, "node", "baileys-bridge.js");
   ```
   The constructor takes `scriptPath` and `ILogger<WhatsAppNodeProcess>`.

6. **Process lifecycle** — `StartAsync(string authDir, CancellationToken ct)`:
   - Spawns `node {scriptPath} --auth-dir {authDir}` using `Process.Start` with `UseShellExecute = false`, `RedirectStandardInput/Output/Error = true`, `CreateNoWindow = true`
   - Starts background `Task` reading stdout line by line via `process.StandardOutput.ReadLineAsync()`, parsing with `ParseLine`, invoking `OnEvent` callback
   - Starts background `Task` reading stderr line by line, forwarding to `ILogger` at `LogLevel.Information` (Baileys logs routine info to stderr)
   - Creates `Channel<string>(new UnboundedChannelOptions())` for stdin serialization
   - Starts background `Task` consuming from the channel, writing each line to `process.StandardInput.WriteLineAsync()` — this is the single writer, ensuring atomicity

7. **`WriteAsync(string jsonLine)`** — writes to the `Channel<string>` writer via `_stdinChannel.Writer.TryWrite(jsonLine)`. Thread-safe by design (Channel is thread-safe).

8. **`StopAsync()`** — writes `{"type":"shutdown"}` to stdin via `WriteAsync`, completes the channel writer (`_stdinChannel.Writer.Complete()`), waits up to 5s for the process to exit (`process.WaitForExitAsync` with `CancellationTokenSource` timeout), force kills via `process.Kill(entireProcessTree: true)` if timeout expires. Disposes the `Process`.

9. **`OnEvent`** — `Action<NodeEvent>?` callback property set by the provider before calling `StartAsync`.

10. **`IAsyncDisposable`** — implements `IAsyncDisposable`. `DisposeAsync` calls `StopAsync` if the process is still running, then disposes the process and cancels background tasks via a `CancellationTokenSource`.

11. **Ping/pong** — not in this task, added in Task 6 with the provider.

- [ ] **Step 4: Run tests — verify they pass**

```bash
cd src/agent && dotnet test --filter "WhatsAppNodeProcessTests"
```

Expected: All 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppNodeProcess.cs src/agent/OpenAgent.Tests/WhatsAppNodeProcessTests.cs
git commit -m "feat: add WhatsAppNodeProcess with JSON line protocol"
```

---

## Task 5: WhatsAppMessageHandler

Processes inbound WhatsApp messages — access control, dedup, sender attribution, LLM call, composing indicator, response send.

**Files:**
- Create: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs`
- Create: `src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs`

- [ ] **Step 1: Write message handler tests**

Create `src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs`. Tests should cover:

1. **Allowed DM message** — sends composing, calls LLM, sends response via node process.
2. **Blocked chat** — silently ignored, no LLM call.
3. **Empty allowlist** — blocks everyone.
4. **Group message** — sender name prefixed: `[Alice] Hello`.
5. **Duplicate message** — same message ID processed twice, second time ignored.
6. **LLM error** — sends error message text back.
7. **Creates conversation** — verifies `GetOrCreate` called with derived conversation ID (`whatsapp:{connectionId}:{chatId}`).

Use the existing `InMemoryConversationStore` and `FakeTelegramTextProvider` (rename/generalize if needed, or create a `FakeTextProvider` that implements `ILlmTextProvider`). For the node process interaction, the handler should accept a delegate or interface for sending messages (similar to how Telegram uses `ITelegramSender`).

The handler needs a way to write to the node process. Define a simple `IWhatsAppSender` interface:

```csharp
public interface IWhatsAppSender
{
    Task SendComposingAsync(string chatId);
    Task SendTextAsync(string chatId, string text);
}
```

Create `FakeWhatsAppSender` in the test fakes that records calls.

- [ ] **Step 2: Run tests — verify they fail**

```bash
cd src/agent && dotnet test --filter "WhatsAppMessageHandlerTests"
```

Expected: Build error — types don't exist yet.

- [ ] **Step 3: Implement IWhatsAppSender**

Create `src/agent/OpenAgent.Channel.WhatsApp/IWhatsAppSender.cs`:

```csharp
namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Abstraction for sending messages to WhatsApp via the Node bridge process.
/// </summary>
public interface IWhatsAppSender
{
    /// <summary>Sends a "composing" presence indicator to the chat.</summary>
    Task SendComposingAsync(string chatId);

    /// <summary>Sends a text message to the chat.</summary>
    Task SendTextAsync(string chatId, string text);
}
```

- [ ] **Step 4: Implement WhatsAppMessageHandler**

Create `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs`. Follow the same pattern as `TelegramMessageHandler` but simpler (no streaming, no drafts):

1. Check access control (`WhatsAppAccessControl.IsAllowed(chatId)`). Silently ignore unauthorized.
2. Check dedup — in-memory `Dictionary<string, DateTime>` of processed message IDs, max 5000 entries, 20-minute TTL. Skip if seen. On every check, evict entries older than 20 minutes if count exceeds 2500 (keeps the dict from growing unbounded without thrashing on every call).
3. Send composing indicator via `IWhatsAppSender.SendComposingAsync(chatId)`.
4. Derive conversation ID: `$"whatsapp:{connectionId}:{chatId}"`.
5. `GetOrCreate` conversation with source `"whatsapp"`, type `ConversationType.Text`.
6. For group messages (`chatId` ends with `@g.us`), prefix user message text with `[{pushName}] `.
7. Call `_textProvider.CompleteAsync(conversation, userMessage, ct)`.
8. Collect all `TextDelta` events into a StringBuilder. Handle `AssistantMessageSaved`.
9. Convert response via `WhatsAppMarkdownConverter.ToWhatsApp()`.
10. Chunk and send each chunk via `IWhatsAppSender.SendTextAsync(chatId, chunk)`.
11. Update assistant message with channel message ID if available.
12. Handle LLM errors — send error text back.

Constructor takes: `IConversationStore`, `ILlmTextProvider`, `string connectionId`, `string providerKey`, `string model`, `WhatsAppOptions`, `ILogger?`.

- [ ] **Step 5: Create FakeWhatsAppSender in test fakes**

Create `src/agent/OpenAgent.Tests/Fakes/FakeWhatsAppSender.cs`:

```csharp
using OpenAgent.Channel.WhatsApp;

namespace OpenAgent.Tests.Fakes;

public class FakeWhatsAppSender : IWhatsAppSender
{
    public List<string> ComposingCalls { get; } = [];
    public List<(string ChatId, string Text)> TextCalls { get; } = [];

    public Task SendComposingAsync(string chatId)
    {
        ComposingCalls.Add(chatId);
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string chatId, string text)
    {
        TextCalls.Add((chatId, text));
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 6: Run tests — verify they pass**

```bash
cd src/agent && dotnet test --filter "WhatsAppMessageHandlerTests"
```

Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/IWhatsAppSender.cs src/agent/OpenAgent.Channel.WhatsApp/WhatsAppMessageHandler.cs src/agent/OpenAgent.Tests/WhatsAppMessageHandlerTests.cs src/agent/OpenAgent.Tests/Fakes/FakeWhatsAppSender.cs
git commit -m "feat: add WhatsAppMessageHandler with dedup, access control, and sender attribution"
```

---

## Task 6: WhatsAppChannelProvider

The main provider that ties everything together — process lifecycle, QR state machine, event routing, ping/pong health monitoring.

**Files:**
- Create: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs`
- Create: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProviderFactory.cs`

- [ ] **Step 1: Implement WhatsAppChannelProvider**

Create `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs`. Implements `IChannelProvider`.

Key state:
- `_state`: enum `{ Unpaired, Pairing, Connected, Failed }`
- `_nodeProcess`: `WhatsAppNodeProcess?`
- `_latestQr`: `string?` — latest QR code data
- `_qrReady`: `TaskCompletionSource?` — signals first QR arrival for the endpoint
- `_handler`: `WhatsAppMessageHandler`
- `_sender`: `WhatsAppNodeProcessSender` — implements `IWhatsAppSender`, delegates to `_nodeProcess.WriteAsync()`

**StartAsync(ct)**:
1. Create the auth directory: `{dataPath}/connections/whatsapp/{connectionId}/`.
2. Check if creds exist (any files in the auth dir).
3. If creds exist: start the node process, set state to Connected once `connected` event received.
4. If no creds: set state to Unpaired. Do NOT start node process.

**StopAsync(ct)**:
1. Stop ping timer.
2. If node process running, call `StopAsync()`.

**StartPairingAsync()** — called by QR endpoint:
1. If already pairing or connected, return immediately.
2. Create `_qrReady = new TaskCompletionSource()`.
3. Start node process.
4. Set state to Pairing.

**GetQrAsync(TimeSpan timeout)** — called by QR endpoint:
1. If connected, return `(Connected, null)`.
2. If unpaired, call `StartPairingAsync()`.
3. If `_latestQr` is not null, return it immediately.
4. Await `_qrReady.Task` with timeout. Return QR or null on timeout.

**Event handling** — callback from `WhatsAppNodeProcess.OnEvent`:
- `qr`: Store in `_latestQr`, complete `_qrReady`.
- `connected`: Set state to Connected, clear QR data.
- `message`: Forward to `_handler.HandleMessageAsync(sender, event)`.
- `disconnected` with reason `loggedOut`: Delete auth dir, set state to Unpaired, stop node process.
- `disconnected` other: Reconnect with backoff.
- `pong`: Record last pong time.

**Ping timer** — every 60s, write `{"type":"ping"}`. If no pong within 10s, force-restart node process.

**Reconnect** — exponential backoff 2s -> 30s, max 10 attempts. Track `_lastConnectedAt` timestamp. When the node process emits `connected`, record `DateTime.UtcNow`. When a restart is needed, check if `(DateTime.UtcNow - _lastConnectedAt) > TimeSpan.FromSeconds(60)` — if so, reset the attempt counter to 0. After exhausting all attempts, set state to `Failed` and log an error. The connection can be restarted via the connection API (`StartConnectionAsync`).

**IAsyncDisposable** — `WhatsAppChannelProvider` implements `IAsyncDisposable`. `DisposeAsync` calls `StopAsync`, disposes the node process and ping timer. `ConnectionManager` should dispose providers on stop if they implement `IAsyncDisposable`.

**WhatsAppNodeProcessSender** — private inner class implementing `IWhatsAppSender`:

```csharp
private sealed class WhatsAppNodeProcessSender : IWhatsAppSender
{
    private readonly WhatsAppNodeProcess _process;
    public WhatsAppNodeProcessSender(WhatsAppNodeProcess process) => _process = process;
    public Task SendComposingAsync(string chatId) => _process.WriteAsync(WhatsAppNodeProcess.FormatComposingCommand(chatId));
    public Task SendTextAsync(string chatId, string text) => _process.WriteAsync(WhatsAppNodeProcess.FormatSendCommand(chatId, text));
}
```

- [ ] **Step 2: Implement WhatsAppChannelProviderFactory**

Create `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProviderFactory.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Connections;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>
/// Creates <see cref="WhatsAppChannelProvider"/> instances from connection configuration.
/// </summary>
public sealed class WhatsAppChannelProviderFactory : IChannelProviderFactory
{
    private readonly IConversationStore _store;
    private readonly ILlmTextProvider _textProvider;
    private readonly string _providerKey;
    private readonly string _model;
    private readonly AgentEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;

    public string Type => "whatsapp";

    public WhatsAppChannelProviderFactory(
        IConversationStore store,
        ILlmTextProvider textProvider,
        string providerKey,
        string model,
        AgentEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _textProvider = textProvider;
        _providerKey = providerKey;
        _model = model;
        _environment = environment;
        _loggerFactory = loggerFactory;
    }

    public IChannelProvider Create(Connection connection)
    {
        var options = JsonSerializer.Deserialize<WhatsAppOptions>(connection.Config,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialize WhatsApp config for connection '{connection.Id}'.");

        var authDir = Path.Combine(_environment.DataPath, "connections", "whatsapp", connection.Id);

        return new WhatsAppChannelProvider(
            options,
            connection.Id,
            authDir,
            _store,
            _textProvider,
            _providerKey,
            _model,
            _loggerFactory);
    }
}
```

- [ ] **Step 3: Verify build**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProvider.cs src/agent/OpenAgent.Channel.WhatsApp/WhatsAppChannelProviderFactory.cs
git commit -m "feat: add WhatsAppChannelProvider with QR state machine, reconnect, and ping/pong"
```

---

## Task 7: QR Code Endpoint

**Files:**
- Create: `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppEndpoints.cs`

- [ ] **Step 1: Implement WhatsApp endpoints**

Create `src/agent/OpenAgent.Channel.WhatsApp/WhatsAppEndpoints.cs`:

```csharp
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.WhatsApp;

/// <summary>Response model for the WhatsApp QR pairing endpoint.</summary>
public sealed record WhatsAppQrResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("qr")] string? Qr);

/// <summary>
/// Registers WhatsApp-specific HTTP endpoints.
/// </summary>
public static class WhatsAppEndpoints
{
    /// <summary>Maps the WhatsApp QR code pairing endpoint.</summary>
    public static WebApplication MapWhatsAppEndpoints(this WebApplication app)
    {
        app.MapGet("/api/connections/{connectionId}/whatsapp/qr", async (
            string connectionId,
            IConnectionManager connectionManager) =>
        {
            // Get running provider — must be a WhatsApp provider
            var provider = connectionManager.GetProvider(connectionId) as WhatsAppChannelProvider;
            if (provider is null)
                return Results.NotFound(new WhatsAppQrResponse("error", null));

            var (status, qrData) = await provider.GetQrAsync(TimeSpan.FromSeconds(30));

            return Results.Ok(new WhatsAppQrResponse(status.ToString().ToLowerInvariant(), qrData));
        })
        .RequireAuthorization();

        return app;
    }
}
```

Note: The endpoint needs the provider to be created even when unpaired. This means `ConnectionManager.StartConnectionAsync` must create the provider for WhatsApp connections regardless of creds state — the provider itself decides whether to start the node process. This is already the case since `StartAsync` on the provider only starts node if creds exist.

- [ ] **Step 2: Verify build**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/WhatsAppEndpoints.cs
git commit -m "feat: add WhatsApp QR code pairing endpoint"
```

---

## Task 8: DI Registration and Wiring

**Files:**
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Register WhatsApp factory and endpoints**

In `src/agent/OpenAgent/Program.cs`, add the WhatsApp factory registration after the Telegram factory. Since `ConnectionManager` takes `IEnumerable<IChannelProviderFactory>`, just add another `AddSingleton<IChannelProviderFactory>`:

```csharp
// After existing Telegram factory registration:
builder.Services.AddSingleton<IChannelProviderFactory>(sp =>
{
    var cfg = sp.GetRequiredService<AgentConfig>();
    var textProvider = sp.GetRequiredKeyedService<ILlmTextProvider>(cfg.TextProvider);
    return new WhatsAppChannelProviderFactory(
        sp.GetRequiredService<IConversationStore>(),
        textProvider,
        cfg.TextProvider,
        cfg.TextModel,
        sp.GetRequiredService<AgentEnvironment>(),
        sp.GetRequiredService<ILoggerFactory>());
});
```

Add endpoint mapping after `app.MapTelegramWebhookEndpoints()`:

```csharp
app.MapWhatsAppEndpoints();
```

Add using at top:

```csharp
using OpenAgent.Channel.WhatsApp;
```

- [ ] **Step 2: Verify build**

```bash
cd src/agent && dotnet build
```

Expected: Build succeeds.

- [ ] **Step 3: Run all tests**

```bash
cd src/agent && dotnet test
```

Expected: All existing + new tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent/Program.cs
git commit -m "feat: wire WhatsApp channel provider into DI and endpoints"
```

---

## Task 9: Telegram Conversation Mapping Fix

Change Telegram to derive per-chat conversation IDs instead of using the connection's single ConversationId.

**Files:**
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs`
- Modify: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProviderFactory.cs`
- Modify: `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`

- [ ] **Step 1: Update TelegramMessageHandler**

In `TelegramMessageHandler.cs`:

1. Add `_connectionId` field (the connection's ID, not the conversation ID).
2. Replace `_conversationId` with a derived ID. In `HandleUpdateAsync`, derive the conversation ID from the chat:

```csharp
var derivedConversationId = $"telegram:{_connectionId}:{chatId}";
```

3. Use `derivedConversationId` instead of `_conversationId` in `GetOrCreate` and when building the user message.
4. Remove the private-chat-only filter — allow group messages too (since each group gets its own conversation now). Keep the access control check.

Update the constructor to accept `connectionId` instead of `conversationId`. The access control should check `userId` (for DMs) or the chat member (for groups) — for now, keep checking `userId` from `message.From.Id` for both DMs and groups.

- [ ] **Step 2: Update TelegramChannelProvider and factory**

In `TelegramChannelProviderFactory.cs`, pass `connection.Id` instead of `connection.ConversationId` to the provider constructor.

In `TelegramChannelProvider.cs`, rename `_conversationId` to `_connectionId` and update the constructor parameter.

- [ ] **Step 3: Update Telegram tests**

In `TelegramMessageHandlerTests.cs`:

1. The `ConversationId` constant becomes `ConnectionId`.
2. Update the handler constructor to pass `ConnectionId` instead of `ConversationId`.
3. Update `HandleUpdateAsync_ValidMessage_CreatesConversation` to check for derived ID `$"telegram:{ConnectionId}:{ChatId}"`.
4. Remove `HandleUpdateAsync_GroupChat_IgnoresMessage` — groups are now handled.
5. Add a new test: `HandleUpdateAsync_GroupChat_CreatesGroupConversation` — verify group messages create a conversation with ID `telegram:{connectionId}:{groupChatId}`.

**Group access control behavior**: Access control still checks `message.From.Id` (the sender), not the group chat ID. In a group, only messages from allowed users get responses. This means the bot only responds in groups where allowed users are posting. This is the intended v1 behavior — group-level allowlisting can come later.

- [ ] **Step 4: Run all tests**

```bash
cd src/agent && dotnet test
```

Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Channel.Telegram/ src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs
git commit -m "feat: derive per-chat conversation IDs for Telegram (DMs and groups)"
```

---

## Task 10: Baileys Bridge Node Script

**Files:**
- Create: `src/agent/OpenAgent.Channel.WhatsApp/node/package.json`
- Create: `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js`

- [ ] **Step 1: Create package.json**

Create `src/agent/OpenAgent.Channel.WhatsApp/node/package.json`:

```json
{
  "name": "baileys-bridge",
  "version": "1.0.0",
  "private": true,
  "type": "module",
  "dependencies": {
    "@whiskeysockets/baileys": "7.0.0-rc.9"
  }
}
```

Pin the exact version — no `^` or `~`. This is the same version OpenClaw uses.

- [ ] **Step 2: Create baileys-bridge.js**

Create `src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js`. This script:

1. **Parses args** — `--auth-dir <path>` for the Baileys auth state directory.

2. **Creates Baileys socket** using `makeWASocket` with `useMultiFileAuthState(authDir)`.

3. **stdout protocol** — writes JSON lines to stdout for:
   - `connection.update` with `qr` -> `{"type":"qr","data":"..."}`
   - `connection.update` with `connection: 'open'` -> `{"type":"connected","jid":"..."}`
   - `connection.update` with `connection: 'close'` -> `{"type":"disconnected","reason":"..."}`
   - `messages.upsert` -> `{"type":"message","id":"...","chatId":"...","from":"...","pushName":"...","text":"...","timestamp":...}`
   - Ping response -> `{"type":"pong"}`

4. **stdin protocol** — reads JSON lines from stdin:
   - `{"type":"send","chatId":"...","text":"..."}` -> `sock.sendMessage(chatId, { text })`
   - `{"type":"composing","chatId":"..."}` -> `sock.sendPresenceUpdate('composing', chatId)`
   - `{"type":"ping"}` -> write `{"type":"pong"}` to stdout
   - `{"type":"shutdown"}` -> graceful close, `process.exit(0)`

5. **Message filtering** — ignores:
   - Status broadcast messages (`chatId === 'status@broadcast'`)
   - Messages from self (`message.key.fromMe`)
   - Non-text messages (for v1)

6. **Logging** — all logs via `console.error()` (stderr), never `console.log()`.

7. **Error handling** — uncaught exceptions logged to stderr and exit with code 1.

8. **Reconnect** — the script does NOT reconnect. If the connection drops, it exits. The .NET side handles reconnection by restarting the process.

Reference OpenClaw files for Baileys patterns:
- `C:\Users\martin\source\repos\OpenClaw\extensions\whatsapp\src\session.ts` — socket creation
- `C:\Users\martin\source\repos\OpenClaw\extensions\whatsapp\src\inbound\monitor.ts` — message event handling
- `C:\Users\martin\source\repos\OpenClaw\extensions\whatsapp\src\auth-store.ts` — auth state

- [ ] **Step 3: Install dependencies**

```bash
cd src/agent/OpenAgent.Channel.WhatsApp/node && npm install
```

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Channel.WhatsApp/node/
git commit -m "feat: add Baileys bridge Node script with JSON line protocol"
```

Notes:
- `node_modules/` should be in `.gitignore`. Verify and add if not.
- `package-lock.json` MUST be committed — it is required by `npm ci` in the Dockerfile for reproducible builds.

---

## Task 11: Dockerfile Update

**Files:**
- Modify: `Dockerfile`

- [ ] **Step 1: Add Node.js to the runtime image and build Baileys deps**

Update `Dockerfile` to:

1. In the runtime stage, install Node.js:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Install Node.js for Baileys bridge
RUN apt-get update && apt-get install -y --no-install-recommends nodejs npm && rm -rf /var/lib/apt/lists/*
```

2. Add a stage to install Node dependencies:

```dockerfile
# --- Baileys bridge dependencies ---
FROM node:22-slim AS baileys-build
WORKDIR /baileys
COPY src/agent/OpenAgent.Channel.WhatsApp/node/package.json src/agent/OpenAgent.Channel.WhatsApp/node/package-lock.json ./
RUN npm ci --omit=dev
```

3. In the runtime stage, copy the installed node_modules and the bridge script:

```dockerfile
COPY --from=baileys-build /baileys/node_modules /app/node/node_modules
COPY src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js /app/node/
COPY src/agent/OpenAgent.Channel.WhatsApp/node/package.json /app/node/
```

Alternatively, since the csproj already copies `node/` to output, the publish output may already include the script. But `node_modules` won't be there — it needs the Docker build stage. Decide on the exact approach during implementation.

- [ ] **Step 2: Build Docker image locally**

```bash
docker build -t openagent-test .
```

Expected: Build succeeds. Image includes both .NET app and Node.js + Baileys deps.

- [ ] **Step 3: Commit**

```bash
git add Dockerfile
git commit -m "feat: add Node.js and Baileys deps to Docker image"
```

---

## Task 12: Integration Smoke Test

Manual verification that the whole flow works end-to-end.

- [ ] **Step 1: Run all unit tests**

```bash
cd src/agent && dotnet test
```

Expected: All tests pass.

- [ ] **Step 2: Start the app locally**

```bash
cd src/agent && dotnet run --project OpenAgent
```

- [ ] **Step 3: Create a WhatsApp connection**

Use the connection API to create a WhatsApp connection:

```bash
curl -X POST http://localhost:8080/api/connections \
  -H "X-Api-Key: <dev-key>" \
  -H "Content-Type: application/json" \
  -d '{"name":"WhatsApp Test","type":"whatsapp","enabled":true,"config":{"allowedChatIds":[]}}'
```

- [ ] **Step 4: Test QR endpoint**

```bash
curl http://localhost:8080/api/connections/<id>/whatsapp/qr -H "X-Api-Key: <dev-key>"
```

Expected: Returns QR data (or pairing status). Verify the Node process starts.

- [ ] **Step 5: Commit final state**

If any fixes were needed during smoke testing, commit them.

---

## Dependency Graph

```
Task 1 (scaffold) ─┬─> Task 2 (options + access control)
                    ├─> Task 3 (markdown converter)
                    └─> Task 4 (node process)
                              │
Task 2 + Task 3 + Task 4 ───> Task 5 (message handler)
                              │
Task 5 ──────────────────────> Task 6 (channel provider + factory)
                              │
Task 6 ──────────────────────> Task 7 (QR endpoint)
                              │
Task 7 ──────────────────────> Task 8 (DI wiring)

Task 1 ──────────────────────> Task 9 (Telegram conversation fix) [independent]

Task 1 ──────────────────────> Task 10 (Node script) [independent]

Task 8 + Task 10 ────────────> Task 11 (Dockerfile)

Task 11 ─────────────────────> Task 12 (smoke test)
```

Tasks 2, 3, 4, 9, and 10 can run in parallel after Task 1. Task 5 depends on 2, 3, and 4. Tasks 9 and 10 are fully independent of the WhatsApp .NET code.
