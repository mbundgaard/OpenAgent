# Telegram Channel Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add Telegram as the first inbound channel — DM text messages with allowlist access control, polling + webhook modes, typing indicator.

**Architecture:** New `OpenAgent.Channel.Telegram` project with `IChannelProvider` contract in `OpenAgent.Contracts`. The channel receives Telegram updates, creates conversations via `GetOrCreate`, calls `ILlmTextProvider.CompleteAsync()`, converts markdown to Telegram HTML via Markdig, and sends replies. `IHostedService` manages the bot lifecycle (polling or webhook).

**Tech Stack:** Telegram.Bot, Markdig, ASP.NET Core IHostedService, xUnit + WebApplicationFactory

**Design doc:** `docs/plans/2026-03-10-telegram-channel-design.md`

---

### Task 1: Project Scaffolding

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/OpenAgent.Channel.Telegram.csproj`
- Modify: `src/agent/OpenAgent.sln`
- Modify: `src/agent/Directory.Packages.props`

**Step 1: Add package versions to central package management**

In `src/agent/Directory.Packages.props`, add inside the `<ItemGroup>`:

```xml
<PackageVersion Include="Telegram.Bot" Version="22.5.0" />
<PackageVersion Include="Markdig" Version="0.40.0" />
```

Check NuGet for latest stable versions at time of implementation — use those instead if newer.

**Step 2: Create the project file**

Create `src/agent/OpenAgent.Channel.Telegram/OpenAgent.Channel.Telegram.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Telegram.Bot" />
    <PackageReference Include="Markdig" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpenAgent.Contracts\OpenAgent.Contracts.csproj" />
    <ProjectReference Include="..\OpenAgent.Models\OpenAgent.Models.csproj" />
  </ItemGroup>
</Project>
```

**Step 3: Add project to solution**

Run:
```bash
cd src/agent && dotnet sln add OpenAgent.Channel.Telegram/OpenAgent.Channel.Telegram.csproj
```

**Step 4: Add project reference from host**

The host project `OpenAgent/OpenAgent.csproj` needs a reference:

```bash
cd src/agent && dotnet add OpenAgent/OpenAgent.csproj reference OpenAgent.Channel.Telegram/OpenAgent.Channel.Telegram.csproj
```

**Step 5: Add test project reference**

```bash
cd src/agent && dotnet add OpenAgent.Tests/OpenAgent.Tests.csproj reference OpenAgent.Channel.Telegram/OpenAgent.Channel.Telegram.csproj
```

**Step 6: Build to verify**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded.

**Step 7: Commit**

```bash
git add -A && git commit -m "chore: scaffold OpenAgent.Channel.Telegram project"
```

---

### Task 2: IChannelProvider Contract

**Files:**
- Create: `src/agent/OpenAgent.Contracts/IChannelProvider.cs`

**Step 1: Create the interface**

Create `src/agent/OpenAgent.Contracts/IChannelProvider.cs`:

```csharp
namespace OpenAgent.Contracts;

/// <summary>
/// Inbound channel that receives messages from external platforms (Telegram, Discord, etc.)
/// and forwards them through the agent pipeline.
/// </summary>
public interface IChannelProvider
{
    /// <summary>Start listening for inbound messages (polling, subscriptions, etc.).</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stop listening and clean up resources.</summary>
    Task StopAsync(CancellationToken ct);
}
```

Note: Does NOT extend `IConfigurable` — channel config uses `IOptions<T>` pattern instead. `IConfigurable` is for runtime admin-endpoint configuration; channels use startup config (appsettings/env vars).

**Step 2: Build to verify**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add IChannelProvider contract"
```

---

### Task 3: TelegramOptions

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/TelegramOptions.cs`

**Step 1: Create the options model**

```csharp
namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Configuration for the Telegram channel, bound from the "Telegram" config section.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>Telegram Bot API token from BotFather.</summary>
    public string? BotToken { get; set; }

    /// <summary>
    /// Telegram user IDs allowed to interact with the bot. Empty = all users blocked.
    /// </summary>
    public List<long> AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Channel mode: "Polling" (default, for local dev) or "Webhook" (for production).
    /// </summary>
    public string Mode { get; set; } = "Polling";

    /// <summary>
    /// Public HTTPS URL for Telegram to send webhook updates to. Required when Mode is "Webhook".
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Secret token for webhook validation. Auto-generated if not set.
    /// </summary>
    public string? WebhookSecret { get; set; }
}
```

**Step 2: Build to verify**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add TelegramOptions config model"
```

---

### Task 4: TelegramAccessControl

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/TelegramAccessControl.cs`
- Create: `src/agent/OpenAgent.Tests/TelegramAccessControlTests.cs`

**Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/TelegramAccessControlTests.cs`:

```csharp
using OpenAgent.Channel.Telegram;

namespace OpenAgent.Tests;

public class TelegramAccessControlTests
{
    [Fact]
    public void EmptyAllowList_BlocksAll()
    {
        var acl = new TelegramAccessControl([]);
        Assert.False(acl.IsAllowed(123456789));
    }

    [Fact]
    public void AllowedUserId_IsAllowed()
    {
        var acl = new TelegramAccessControl([123456789, 987654321]);
        Assert.True(acl.IsAllowed(123456789));
        Assert.True(acl.IsAllowed(987654321));
    }

    [Fact]
    public void UnknownUserId_IsBlocked()
    {
        var acl = new TelegramAccessControl([123456789]);
        Assert.False(acl.IsAllowed(999999999));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `cd src/agent && dotnet test --filter TelegramAccessControlTests`
Expected: FAIL — `TelegramAccessControl` does not exist.

**Step 3: Write minimal implementation**

Create `src/agent/OpenAgent.Channel.Telegram/TelegramAccessControl.cs`:

```csharp
namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Checks whether a Telegram user ID is in the allowlist.
/// Empty allowlist = all users blocked (secure by default).
/// </summary>
public sealed class TelegramAccessControl
{
    private readonly HashSet<long> _allowedUserIds;

    public TelegramAccessControl(IEnumerable<long> allowedUserIds)
    {
        _allowedUserIds = new HashSet<long>(allowedUserIds);
    }

    /// <summary>Returns true if the user ID is in the allowlist.</summary>
    public bool IsAllowed(long userId) => _allowedUserIds.Contains(userId);
}
```

**Step 4: Run tests to verify they pass**

Run: `cd src/agent && dotnet test --filter TelegramAccessControlTests`
Expected: 3 passed.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add TelegramAccessControl with allowlist"
```

---

### Task 5: TelegramMarkdownConverter

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/TelegramMarkdownConverter.cs`
- Create: `src/agent/OpenAgent.Tests/TelegramMarkdownConverterTests.cs`

**Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/TelegramMarkdownConverterTests.cs`:

```csharp
using OpenAgent.Channel.Telegram;

namespace OpenAgent.Tests;

public class TelegramMarkdownConverterTests
{
    [Fact]
    public void PlainText_PassesThrough()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("Hello world");
        Assert.Equal("Hello world", result.Trim());
    }

    [Fact]
    public void Bold_ConvertsToHtmlB()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("This is **bold** text");
        Assert.Contains("<b>bold</b>", result);
    }

    [Fact]
    public void Italic_ConvertsToHtmlI()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("This is *italic* text");
        Assert.Contains("<i>italic</i>", result);
    }

    [Fact]
    public void InlineCode_ConvertsToHtmlCode()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("Use `dotnet build` here");
        Assert.Contains("<code>dotnet build</code>", result);
    }

    [Fact]
    public void CodeBlock_ConvertsToPreCode()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("```csharp\nvar x = 1;\n```");
        Assert.Contains("<pre><code class=\"language-csharp\">", result);
        Assert.Contains("var x = 1;", result);
        Assert.Contains("</code></pre>", result);
    }

    [Fact]
    public void CodeBlock_NoLanguage_ConvertsToPreCode()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("```\nvar x = 1;\n```");
        Assert.Contains("<pre><code>", result);
        Assert.Contains("var x = 1;", result);
    }

    [Fact]
    public void Link_ConvertsToAnchor()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("[click here](https://example.com)");
        Assert.Contains("<a href=\"https://example.com\">click here</a>", result);
    }

    [Fact]
    public void Link_JavascriptScheme_StrippedToTextOnly()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("[click](javascript:alert(1))");
        Assert.DoesNotContain("javascript:", result);
        Assert.Contains("click", result);
    }

    [Fact]
    public void Strikethrough_ConvertsToHtmlS()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("This is ~~deleted~~ text");
        Assert.Contains("<s>deleted</s>", result);
    }

    [Fact]
    public void HtmlEntities_AreEscaped()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("Use <div> & \"quotes\"");
        Assert.Contains("&lt;div&gt;", result);
        Assert.Contains("&amp;", result);
    }

    [Fact]
    public void NestedFormatting_Works()
    {
        var result = TelegramMarkdownConverter.ToTelegramHtml("This is **bold and *italic* inside**");
        Assert.Contains("<b>", result);
        Assert.Contains("<i>", result);
    }

    [Fact]
    public void ChunkMarkdown_ShortText_SingleChunk()
    {
        var chunks = TelegramMarkdownConverter.ChunkMarkdown("Hello world", 4096);
        Assert.Single(chunks);
        Assert.Equal("Hello world", chunks[0]);
    }

    [Fact]
    public void ChunkMarkdown_LongText_SplitsAtParagraphBoundary()
    {
        var paragraph1 = new string('A', 2000);
        var paragraph2 = new string('B', 2000);
        var text = $"{paragraph1}\n\n{paragraph2}";

        var chunks = TelegramMarkdownConverter.ChunkMarkdown(text, 2500);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(paragraph1, chunks[0]);
        Assert.Equal(paragraph2, chunks[1]);
    }

    [Fact]
    public void ChunkMarkdown_NoParagraphBreak_SplitsAtNewline()
    {
        var line1 = new string('A', 2000);
        var line2 = new string('B', 2000);
        var text = $"{line1}\n{line2}";

        var chunks = TelegramMarkdownConverter.ChunkMarkdown(text, 2500);
        Assert.Equal(2, chunks.Count);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `cd src/agent && dotnet test --filter TelegramMarkdownConverterTests`
Expected: FAIL — `TelegramMarkdownConverter` does not exist.

**Step 3: Write the implementation**

Create `src/agent/OpenAgent.Channel.Telegram/TelegramMarkdownConverter.cs`:

```csharp
using System.Text;
using System.Web;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Converts LLM markdown output to Telegram-compatible HTML using Markdig AST.
/// Falls back gracefully — plain text is always valid output.
/// </summary>
public static class TelegramMarkdownConverter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras() // strikethrough
        .Build();

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https"
    };

    /// <summary>
    /// Converts markdown text to Telegram HTML. Returns plain text if conversion fails.
    /// </summary>
    public static string ToTelegramHtml(string markdown)
    {
        try
        {
            var document = Markdown.Parse(markdown, Pipeline);
            var sb = new StringBuilder();
            RenderBlock(document, sb);
            return sb.ToString().TrimEnd();
        }
        catch
        {
            // Fallback: return HTML-escaped plain text
            return HttpUtility.HtmlEncode(markdown);
        }
    }

    /// <summary>
    /// Splits markdown into chunks that will each fit within maxLength after HTML conversion.
    /// Splits at paragraph boundaries (double newline), then single newlines, then hard cut.
    /// </summary>
    public static IReadOnlyList<string> ChunkMarkdown(string markdown, int maxLength)
    {
        if (markdown.Length <= maxLength)
            return [markdown];

        var chunks = new List<string>();
        var remaining = markdown;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxLength)
            {
                chunks.Add(remaining);
                break;
            }

            // Try paragraph boundary
            var splitIndex = remaining.LastIndexOf("\n\n", maxLength, StringComparison.Ordinal);
            if (splitIndex <= 0)
            {
                // Try single newline
                splitIndex = remaining.LastIndexOf('\n', maxLength);
            }
            if (splitIndex <= 0)
            {
                // Hard cut
                splitIndex = maxLength;
            }

            chunks.Add(remaining[..splitIndex].TrimEnd());
            remaining = remaining[(splitIndex < remaining.Length ? splitIndex : remaining.Length)..].TrimStart('\n');
        }

        return chunks;
    }

    private static void RenderBlock(MarkdownObject block, StringBuilder sb)
    {
        switch (block)
        {
            case MarkdownDocument doc:
                foreach (var child in doc)
                    RenderBlock(child, sb);
                break;

            case HeadingBlock heading:
                sb.Append("<b>");
                RenderInlines(heading.Inline, sb);
                sb.Append("</b>\n\n");
                break;

            case ParagraphBlock paragraph:
                RenderInlines(paragraph.Inline, sb);
                sb.Append("\n\n");
                break;

            case FencedCodeBlock fencedCode:
                var lang = fencedCode.Info;
                if (!string.IsNullOrEmpty(lang))
                    sb.Append($"<pre><code class=\"language-{HttpUtility.HtmlEncode(lang)}\">");
                else
                    sb.Append("<pre><code>");
                // Extract code lines
                foreach (var line in fencedCode.Lines)
                {
                    var lineText = line.ToString();
                    sb.Append(HttpUtility.HtmlEncode(lineText));
                    sb.Append('\n');
                }
                // Remove trailing newline inside code block
                if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
                    sb.Length--;
                sb.Append("</code></pre>\n\n");
                break;

            case CodeBlock codeBlock:
                sb.Append("<pre><code>");
                foreach (var line in codeBlock.Lines)
                    sb.Append(HttpUtility.HtmlEncode(line.ToString()));
                sb.Append("</code></pre>\n\n");
                break;

            case ListBlock list:
                foreach (var item in list)
                    RenderBlock(item, sb);
                break;

            case ListItemBlock listItem:
                sb.Append("- ");
                foreach (var child in listItem)
                {
                    if (child is ParagraphBlock p)
                        RenderInlines(p.Inline, sb);
                    else
                        RenderBlock(child, sb);
                }
                sb.Append('\n');
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                    RenderBlock(child, sb);
                break;

            default:
                // Skip unsupported blocks
                break;
        }
    }

    private static void RenderInlines(ContainerInline? container, StringBuilder sb)
    {
        if (container is null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(HttpUtility.HtmlEncode(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                    var tag = emphasis.DelimiterCount == 2 ? "b" :
                              emphasis.DelimiterChar == '~' ? "s" : "i";
                    sb.Append($"<{tag}>");
                    RenderInlines(emphasis, sb);
                    sb.Append($"</{tag}>");
                    break;

                case CodeInline code:
                    sb.Append("<code>");
                    sb.Append(HttpUtility.HtmlEncode(code.Content));
                    sb.Append("</code>");
                    break;

                case LinkInline link:
                    if (link.Url is not null && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
                        && AllowedSchemes.Contains(uri.Scheme))
                    {
                        sb.Append($"<a href=\"{HttpUtility.HtmlEncode(link.Url)}\">");
                        RenderInlines(link, sb);
                        sb.Append("</a>");
                    }
                    else
                    {
                        // Unsafe scheme — render text only
                        RenderInlines(link, sb);
                    }
                    break;

                case LineBreakInline:
                    sb.Append('\n');
                    break;

                default:
                    break;
            }
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `cd src/agent && dotnet test --filter TelegramMarkdownConverterTests`
Expected: All passed. Some tests may need assertion adjustments based on exact Markdig output (whitespace, trailing newlines). Fix as needed.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add TelegramMarkdownConverter with Markdig"
```

---

### Task 6: TelegramMessageHandler

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`
- Create: `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`

**Step 1: Write the failing tests**

Create `src/agent/OpenAgent.Tests/TelegramMessageHandlerTests.cs`:

```csharp
using OpenAgent.Channel.Telegram;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Tests;

public class TelegramMessageHandlerTests
{
    [Fact]
    public async Task HandleUpdate_PrivateTextMessage_AllowedUser_CallsProviderAndReplies()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Hello back!");
        var botClient = new FakeTelegramBotClient();
        var options = new TelegramOptions { AllowedUserIds = [42] };
        var handler = new TelegramMessageHandler(store, provider, options);

        var update = CreateTextUpdate(chatId: 42, userId: 42, text: "Hi there");

        await handler.HandleUpdateAsync(botClient, update, CancellationToken.None);

        Assert.Single(botClient.SentMessages);
        Assert.Contains("Hello back!", botClient.SentMessages[0].Text);
    }

    [Fact]
    public async Task HandleUpdate_UnauthorizedUser_NoReply()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Should not see this");
        var botClient = new FakeTelegramBotClient();
        var options = new TelegramOptions { AllowedUserIds = [42] };
        var handler = new TelegramMessageHandler(store, provider, options);

        var update = CreateTextUpdate(chatId: 99, userId: 99, text: "Hi");

        await handler.HandleUpdateAsync(botClient, update, CancellationToken.None);

        Assert.Empty(botClient.SentMessages);
    }

    [Fact]
    public async Task HandleUpdate_GroupMessage_Ignored()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Should not see this");
        var botClient = new FakeTelegramBotClient();
        var options = new TelegramOptions { AllowedUserIds = [42] };
        var handler = new TelegramMessageHandler(store, provider, options);

        var update = CreateTextUpdate(chatId: -100123, userId: 42, text: "Hi", chatType: ChatType.Group);

        await handler.HandleUpdateAsync(botClient, update, CancellationToken.None);

        Assert.Empty(botClient.SentMessages);
    }

    [Fact]
    public async Task HandleUpdate_NoText_Ignored()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Should not see this");
        var botClient = new FakeTelegramBotClient();
        var options = new TelegramOptions { AllowedUserIds = [42] };
        var handler = new TelegramMessageHandler(store, provider, options);

        var update = new Update { Message = new Telegram.Bot.Types.Message
        {
            Chat = new Chat { Id = 42, Type = ChatType.Private },
            From = new User { Id = 42, FirstName = "Test" }
            // No Text property set
        }};

        await handler.HandleUpdateAsync(botClient, update, CancellationToken.None);

        Assert.Empty(botClient.SentMessages);
    }

    [Fact]
    public async Task HandleUpdate_CreatesConversationWithTelegramPrefix()
    {
        var store = new InMemoryConversationStore();
        var provider = new FakeTelegramTextProvider("Response");
        var botClient = new FakeTelegramBotClient();
        var options = new TelegramOptions { AllowedUserIds = [42] };
        var handler = new TelegramMessageHandler(store, provider, options);

        var update = CreateTextUpdate(chatId: 42, userId: 42, text: "Hi");

        await handler.HandleUpdateAsync(botClient, update, CancellationToken.None);

        var conversation = store.Get("telegram-42");
        Assert.NotNull(conversation);
        Assert.Equal("telegram", conversation.Source);
        Assert.Equal(ConversationType.Text, conversation.Type);
    }

    [Fact]
    public async Task HandleUpdate_LlmFailure_SendsErrorMessage()
    {
        var store = new InMemoryConversationStore();
        var provider = new ThrowingTextProvider();
        var botClient = new FakeTelegramBotClient();
        var options = new TelegramOptions { AllowedUserIds = [42] };
        var handler = new TelegramMessageHandler(store, provider, options);

        var update = CreateTextUpdate(chatId: 42, userId: 42, text: "Hi");

        await handler.HandleUpdateAsync(botClient, update, CancellationToken.None);

        Assert.Single(botClient.SentMessages);
        Assert.Contains("couldn't process", botClient.SentMessages[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    // -- Helpers --

    private static Update CreateTextUpdate(long chatId, long userId, string text,
        ChatType chatType = ChatType.Private)
    {
        return new Update
        {
            Message = new Telegram.Bot.Types.Message
            {
                Text = text,
                Chat = new Chat { Id = chatId, Type = chatType },
                From = new User { Id = userId, FirstName = "TestUser" }
            }
        };
    }
}
```

The test helpers (`FakeTelegramBotClient`, `FakeTelegramTextProvider`, `ThrowingTextProvider`, `InMemoryConversationStore`) need to be created. These are non-trivial fakes — particularly `FakeTelegramBotClient` needs to capture `SendMessage` calls. The Telegram.Bot library uses `ITelegramBotClient`. Create these as inner classes or a shared test helper file.

Create `src/agent/OpenAgent.Tests/Fakes/FakeTelegramBotClient.cs` — this must implement `ITelegramBotClient` and capture sent messages. This is the trickiest fake because `ITelegramBotClient` has many methods. Use a minimal implementation that only implements what we call (SendMessage, SendChatAction) and throws `NotImplementedException` on everything else. **Check the Telegram.Bot library's `ITelegramBotClient` interface at implementation time** — the exact method signatures depend on the version installed.

Create `src/agent/OpenAgent.Tests/Fakes/FakeTelegramTextProvider.cs`:

```csharp
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OpenAgent.Tests.Fakes;

public sealed class FakeTelegramTextProvider : ILlmTextProvider
{
    private readonly string _response;
    public string Key => "text-provider";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }

    public FakeTelegramTextProvider(string response) => _response = response;

    public async IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextDelta(_response);
        await Task.CompletedTask;
    }
}

public sealed class ThrowingTextProvider : ILlmTextProvider
{
    public string Key => "text-provider";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }

    public IAsyncEnumerable<CompletionEvent> CompleteAsync(
        Conversation conversation, string userInput, CancellationToken ct = default)
    {
        throw new InvalidOperationException("LLM is down");
    }
}
```

Create `src/agent/OpenAgent.Tests/Fakes/InMemoryConversationStore.cs`:

```csharp
using OpenAgent.Contracts;
using OpenAgent.Models.Conversations;
using OpenAgent.Models.Providers;
using System.Text.Json;

namespace OpenAgent.Tests.Fakes;

public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<string, Conversation> _conversations = new();
    private readonly Dictionary<string, List<Message>> _messages = new();

    public string Key => "conversation-store";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];
    public void Configure(JsonElement configuration) { }

    public Conversation GetOrCreate(string conversationId, string source, ConversationType type)
    {
        if (!_conversations.TryGetValue(conversationId, out var conv))
        {
            conv = new Conversation { Id = conversationId, Source = source, Type = type };
            _conversations[conversationId] = conv;
            _messages[conversationId] = [];
        }
        return conv;
    }

    public Conversation? Get(string conversationId) =>
        _conversations.GetValueOrDefault(conversationId);

    public IReadOnlyList<Conversation> GetAll() => _conversations.Values.ToList();
    public void Update(Conversation conversation) => _conversations[conversation.Id] = conversation;
    public bool Delete(string conversationId) => _conversations.Remove(conversationId);
    public void AddMessage(string conversationId, Message message)
    {
        if (!_messages.ContainsKey(conversationId))
            _messages[conversationId] = [];
        _messages[conversationId].Add(message);
    }
    public IReadOnlyList<Message> GetMessages(string conversationId) =>
        _messages.GetValueOrDefault(conversationId) ?? [];
}
```

**Step 2: Run tests to verify they fail**

Run: `cd src/agent && dotnet test --filter TelegramMessageHandlerTests`
Expected: FAIL — `TelegramMessageHandler` does not exist.

**Step 3: Write the implementation**

Create `src/agent/OpenAgent.Channel.Telegram/TelegramMessageHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Models.Common;
using OpenAgent.Models.Conversations;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Processes a single Telegram update: filters, routes to conversation,
/// calls the LLM, and sends the reply.
/// </summary>
public sealed class TelegramMessageHandler
{
    private const int TelegramMessageLimit = 4096;

    private readonly IConversationStore _store;
    private readonly ILlmTextProvider _textProvider;
    private readonly TelegramAccessControl _accessControl;
    private readonly ILogger<TelegramMessageHandler>? _logger;

    public TelegramMessageHandler(
        IConversationStore store,
        ILlmTextProvider textProvider,
        TelegramOptions options,
        ILogger<TelegramMessageHandler>? logger = null)
    {
        _store = store;
        _textProvider = textProvider;
        _accessControl = new TelegramAccessControl(options.AllowedUserIds);
        _logger = logger;
    }

    /// <summary>
    /// Handles a single Telegram update. Filters non-text, non-private, and unauthorized messages.
    /// </summary>
    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        // Phase 1: only private text messages
        var message = update.Message;
        if (message?.Text is null) return;
        if (message.Chat.Type != ChatType.Private) return;
        if (message.From is null) return;

        // Access control
        if (!_accessControl.IsAllowed(message.From.Id))
        {
            _logger?.LogDebug("Telegram: blocked message from unauthorized user {UserId}", message.From.Id);
            return;
        }

        var chatId = message.Chat.Id;
        var conversationId = $"telegram-{chatId}";
        var userText = message.Text;

        try
        {
            // Typing indicator
            await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            // Get or create conversation
            var conversation = _store.GetOrCreate(conversationId, "telegram", ConversationType.Text);

            // Run LLM completion
            var responseBuilder = new StringBuilder();
            await foreach (var evt in _textProvider.CompleteAsync(conversation, userText, ct))
            {
                if (evt is TextDelta delta)
                    responseBuilder.Append(delta.Content);
            }

            var responseText = responseBuilder.ToString();
            if (string.IsNullOrWhiteSpace(responseText))
                return;

            // Chunk and send
            await SendResponseAsync(botClient, chatId, responseText, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Telegram: error processing message from chat {ChatId}", chatId);
            await TrySendPlainTextAsync(botClient, chatId,
                "Sorry, I couldn't process that. Please try again.", ct);
        }
    }

    private static async Task SendResponseAsync(ITelegramBotClient botClient, long chatId,
        string markdownResponse, CancellationToken ct)
    {
        var chunks = TelegramMarkdownConverter.ChunkMarkdown(markdownResponse, TelegramMessageLimit);

        foreach (var chunk in chunks)
        {
            var html = TelegramMarkdownConverter.ToTelegramHtml(chunk);

            try
            {
                await SendWithRetryAsync(botClient, chatId, html, ParseMode.Html, ct);
            }
            catch
            {
                // HTML parse failed — fall back to plain text
                await TrySendPlainTextAsync(botClient, chatId, chunk, ct);
            }
        }
    }

    private static async Task SendWithRetryAsync(ITelegramBotClient botClient, long chatId,
        string text, ParseMode parseMode, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await botClient.SendMessage(chatId, text, parseMode: parseMode, cancellationToken: ct);
                return;
            }
            catch when (attempt < maxAttempts)
            {
                await Task.Delay(delay, ct);
                delay *= 2; // exponential backoff: 1s, 2s, 4s
            }
        }
    }

    private static async Task TrySendPlainTextAsync(ITelegramBotClient botClient, long chatId,
        string text, CancellationToken ct)
    {
        try
        {
            await botClient.SendMessage(chatId, text, cancellationToken: ct);
        }
        catch
        {
            // Final fallback — can't send anything. Already logged at call site.
        }
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `cd src/agent && dotnet test --filter TelegramMessageHandlerTests`
Expected: All passed. Note: the exact `ITelegramBotClient` method signatures depend on the Telegram.Bot version. Adjust `FakeTelegramBotClient` and `TelegramMessageHandler` to match the actual API at implementation time.

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: add TelegramMessageHandler with filtering, LLM call, and retry"
```

---

### Task 7: TelegramChannelProvider + TelegramBotService

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/TelegramChannelProvider.cs`
- Create: `src/agent/OpenAgent.Channel.Telegram/TelegramBotService.cs`

**Step 1: Create TelegramChannelProvider**

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Telegram channel provider. Creates the bot client and manages its lifecycle.
/// Delegates message handling to TelegramMessageHandler.
/// </summary>
public sealed class TelegramChannelProvider : IChannelProvider
{
    private readonly TelegramOptions _options;
    private readonly TelegramMessageHandler _handler;
    private readonly ILogger<TelegramChannelProvider> _logger;
    private TelegramBotClient? _botClient;
    private CancellationTokenSource? _pollingCts;

    public TelegramChannelProvider(
        IOptions<TelegramOptions> options,
        IConversationStore store,
        ILlmTextProvider textProvider,
        ILogger<TelegramChannelProvider> logger,
        ILogger<TelegramMessageHandler> handlerLogger)
    {
        _options = options.Value;
        _handler = new TelegramMessageHandler(store, textProvider, _options, handlerLogger);
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            if (_options.Mode.Equals("Webhook", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Telegram BotToken is required when Mode is 'Webhook'. Set Telegram__BotToken.");

            _logger.LogWarning("Telegram: BotToken not configured — channel disabled");
            return;
        }

        _botClient = new TelegramBotClient(_options.BotToken);

        if (_options.Mode.Equals("Webhook", StringComparison.OrdinalIgnoreCase))
        {
            // Webhook mode: register webhook URL with Telegram
            var secret = _options.WebhookSecret ?? Guid.NewGuid().ToString("N");
            _options.WebhookSecret = secret; // Store for validation

            await _botClient.SetWebhook(
                _options.WebhookUrl!,
                secretToken: secret,
                cancellationToken: ct);

            _logger.LogInformation("Telegram: webhook registered at {Url}", _options.WebhookUrl);
        }
        else
        {
            // Polling mode
            _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message]
            };

            _botClient.StartReceiving(
                updateHandler: _handler.HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: _pollingCts.Token);

            _logger.LogInformation("Telegram: polling started");
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_botClient is null) return;

        if (_options.Mode.Equals("Webhook", StringComparison.OrdinalIgnoreCase))
        {
            await _botClient.DeleteWebhook(cancellationToken: ct);
            _logger.LogInformation("Telegram: webhook deleted");
        }
        else
        {
            _pollingCts?.Cancel();
            _logger.LogInformation("Telegram: polling stopped");
        }
    }

    /// <summary>The bot client instance, for use by webhook endpoint.</summary>
    public TelegramBotClient? BotClient => _botClient;

    /// <summary>The message handler, for use by webhook endpoint.</summary>
    public TelegramMessageHandler Handler => _handler;

    /// <summary>The webhook secret, for validation.</summary>
    public string? WebhookSecret => _options.WebhookSecret;

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
    {
        _logger.LogError(exception, "Telegram: polling error");
        return Task.CompletedTask;
    }
}
```

**Step 2: Create TelegramBotService**

```csharp
using Microsoft.Extensions.Hosting;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// ASP.NET Core hosted service that starts and stops the Telegram channel provider.
/// </summary>
public sealed class TelegramBotService : IHostedService
{
    private readonly TelegramChannelProvider _channelProvider;

    public TelegramBotService(TelegramChannelProvider channelProvider)
    {
        _channelProvider = channelProvider;
    }

    public Task StartAsync(CancellationToken ct) => _channelProvider.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => _channelProvider.StopAsync(ct);
}
```

**Step 3: Build to verify**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add -A && git commit -m "feat: add TelegramChannelProvider and TelegramBotService"
```

---

### Task 8: TelegramWebhookEndpoints

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/TelegramWebhookEndpoints.cs`

**Step 1: Create the webhook endpoint**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// Registers the Telegram webhook endpoint for receiving updates from Telegram servers.
/// </summary>
public static class TelegramWebhookEndpoints
{
    /// <summary>
    /// Maps POST /api/telegram/webhook — receives Telegram updates in webhook mode.
    /// Validates the secret token header. Anonymous (Telegram calls this, not users).
    /// </summary>
    public static WebApplication MapTelegramWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/telegram/webhook", async (
            HttpContext context,
            TelegramChannelProvider channelProvider,
            CancellationToken ct) =>
        {
            // Only active in webhook mode
            if (channelProvider.BotClient is null)
                return Results.NotFound();

            // Validate secret token
            var secretHeader = context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].FirstOrDefault();
            if (secretHeader != channelProvider.WebhookSecret)
                return Results.Unauthorized();

            // Deserialize the update
            var update = await context.Request.ReadFromJsonAsync<Update>(ct);
            if (update is null)
                return Results.BadRequest();

            // Process asynchronously (don't block Telegram)
            _ = Task.Run(() => channelProvider.Handler.HandleUpdateAsync(
                channelProvider.BotClient, update, CancellationToken.None), CancellationToken.None);

            return Results.Ok();
        }).AllowAnonymous();

        return app;
    }
}
```

**Step 2: Build to verify**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add Telegram webhook endpoint"
```

---

### Task 9: TelegramServiceExtensions + Host Integration

**Files:**
- Create: `src/agent/OpenAgent.Channel.Telegram/TelegramServiceExtensions.cs`
- Modify: `src/agent/OpenAgent/Program.cs`

**Step 1: Create the extension methods**

Create `src/agent/OpenAgent.Channel.Telegram/TelegramServiceExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts;

namespace OpenAgent.Channel.Telegram;

/// <summary>
/// DI registration for the Telegram channel.
/// </summary>
public static class TelegramServiceExtensions
{
    /// <summary>
    /// Registers the Telegram channel provider, bot service, and configuration.
    /// </summary>
    public static IServiceCollection AddTelegramChannel(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TelegramOptions>(configuration.GetSection("Telegram"));
        services.AddSingleton<TelegramChannelProvider>();
        services.AddSingleton<IChannelProvider>(sp => sp.GetRequiredService<TelegramChannelProvider>());
        services.AddHostedService<TelegramBotService>();
        return services;
    }
}
```

**Step 2: Wire into Program.cs**

In `src/agent/OpenAgent/Program.cs`, add the using and registration:

Add to usings:
```csharp
using OpenAgent.Channel.Telegram;
```

Add after the existing service registrations (after `AddApiKeyAuth`):
```csharp
builder.Services.AddTelegramChannel(builder.Configuration);
```

Add after the existing endpoint mappings (after `MapAdminEndpoints`):
```csharp
app.MapTelegramWebhookEndpoints();
```

**Step 3: Build to verify**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded.

**Step 4: Run all existing tests to verify no regressions**

Run: `cd src/agent && dotnet test`
Expected: All tests pass. The Telegram bot service will start but immediately exit (no BotToken configured in test environment).

**Step 5: Commit**

```bash
git add -A && git commit -m "feat: wire Telegram channel into host"
```

---

### Task 10: Integration Test — End-to-End Webhook Flow

**Files:**
- Create: `src/agent/OpenAgent.Tests/TelegramWebhookEndpointTests.cs`

**Step 1: Write the integration test**

Create `src/agent/OpenAgent.Tests/TelegramWebhookEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Channel.Telegram;
using OpenAgent.Contracts;
using OpenAgent.Tests.Fakes;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpenAgent.Tests;

public class TelegramWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TelegramWebhookEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace text provider with fake
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ILlmTextProvider));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddSingleton<ILlmTextProvider, FakeTelegramTextProvider>(
                    _ => new FakeTelegramTextProvider("Test response"));
            });
            builder.UseSetting("Telegram:BotToken", "fake-bot-token");
            builder.UseSetting("Telegram:Mode", "Webhook");
            builder.UseSetting("Telegram:WebhookUrl", "https://example.com/api/telegram/webhook");
            builder.UseSetting("Telegram:WebhookSecret", "test-secret");
            builder.UseSetting("Telegram:AllowedUserIds:0", "42");
        });
    }

    [Fact]
    public async Task Webhook_ValidUpdate_Returns200()
    {
        var client = _factory.CreateClient();

        var update = new Update
        {
            Message = new Message
            {
                Text = "Hello",
                Chat = new Chat { Id = 42, Type = ChatType.Private },
                From = new User { Id = 42, FirstName = "Test" }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "test-secret");
        request.Content = JsonContent.Create(update);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_MissingSecret_Returns401()
    {
        var client = _factory.CreateClient();

        var update = new Update
        {
            Message = new Message
            {
                Text = "Hello",
                Chat = new Chat { Id = 42, Type = ChatType.Private },
                From = new User { Id = 42, FirstName = "Test" }
            }
        };

        var response = await client.PostAsJsonAsync("/api/telegram/webhook", update);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_WrongSecret_Returns401()
    {
        var client = _factory.CreateClient();

        var update = new Update { Message = new Message
        {
            Text = "Hello",
            Chat = new Chat { Id = 42, Type = ChatType.Private },
            From = new User { Id = 42, FirstName = "Test" }
        }};

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "wrong-secret");
        request.Content = JsonContent.Create(update);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_IsAnonymous_NoApiKeyNeeded()
    {
        var client = _factory.CreateClient();
        // No X-Api-Key header — webhook should still work

        var update = new Update { Message = new Message
        {
            Text = "Hello",
            Chat = new Chat { Id = 42, Type = ChatType.Private },
            From = new User { Id = 42, FirstName = "Test" }
        }};

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "test-secret");
        request.Content = JsonContent.Create(update);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

Note: The webhook integration test verifies HTTP-level behavior. It won't actually send messages to Telegram (the bot client connects to real Telegram API which will fail with a fake token). The test validates: routing, auth, secret validation, and that the endpoint is anonymous. The `TelegramBotService.StartAsync` may throw or warn on startup with a fake token — the test factory may need to suppress or mock the hosted service. Adjust at implementation time.

**Step 2: Run the integration tests**

Run: `cd src/agent && dotnet test --filter TelegramWebhookEndpointTests`
Expected: All pass (or adjust for Telegram.Bot startup behavior with fake tokens).

**Step 3: Run all tests**

Run: `cd src/agent && dotnet test`
Expected: All tests pass.

**Step 4: Commit**

```bash
git add -A && git commit -m "test: add Telegram webhook endpoint integration tests"
```

---

### Task 11: Final Verification

**Step 1: Full build**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded, 0 warnings (or only pre-existing warnings).

**Step 2: Full test suite**

Run: `cd src/agent && dotnet test`
Expected: All tests pass.

**Step 3: Verify local polling startup**

Add to `src/agent/OpenAgent/appsettings.Development.json` (if it exists):

```json
{
  "Telegram": {
    "BotToken": "",
    "AllowedUserIds": [],
    "Mode": "Polling"
  }
}
```

Run: `cd src/agent && dotnet run --project OpenAgent`
Expected: App starts. Log contains "Telegram: BotToken not configured — channel disabled". No crash.

**Step 4: Commit any remaining changes**

```bash
git add -A && git commit -m "chore: finalize Telegram channel Phase 1"
```
