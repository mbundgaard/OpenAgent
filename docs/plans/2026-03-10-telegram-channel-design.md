# Telegram Channel — Design

## Overview

Add Telegram as the first inbound channel for OpenAgent. Introduces `IChannelProvider` contract and a self-contained `OpenAgent.Channel.Telegram` project.

Phase 1 scope: DM text messages, allowlist access control, polling + webhook modes, typing indicator. No streaming, groups, media, or bot commands.

## IChannelProvider Contract

Lives in `OpenAgent.Contracts`:

```csharp
public interface IChannelProvider
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

Does not extend `IConfigurable` — channel config uses `IOptions<T>` via the standard ASP.NET config pipeline (appsettings/env vars), not the runtime admin-endpoint pattern used by LLM providers.

A channel starts, listens for inbound messages, creates conversations via `IConversationStore.GetOrCreate()`, calls `ILlmTextProvider.CompleteAsync()`, and sends responses back through its platform API.

No shared envelope type yet — extract when the second channel arrives.

## Project Structure

```
OpenAgent.Channel.Telegram/
  TelegramChannelProvider.cs      IChannelProvider impl, bot lifecycle
  TelegramBotService.cs           IHostedService (start/stop polling or webhook)
  TelegramMessageHandler.cs       Receive → conversation → LLM → reply
  TelegramWebhookEndpoints.cs     POST /api/telegram/webhook
  TelegramAccessControl.cs        Allowlist check
  TelegramMarkdownConverter.cs    LLM markdown → Telegram HTML
  TelegramOptions.cs              Config model
  TelegramServiceExtensions.cs    AddTelegramChannel() + MapTelegramWebhookEndpoints()
```

## Message Flow

1. Telegram delivers `Update` (via polling or webhook)
2. `TelegramMessageHandler` extracts text, checks access control
3. Calls `store.GetOrCreate($"telegram-{chatId}", "telegram", ConversationType.Text)`
4. Sends typing indicator (`ChatAction.Typing`)
5. Calls `textProvider.CompleteAsync(conversation, messageText, ct)`
6. Collects `CompletionEvent`s, concatenates `TextDelta` content
7. Converts markdown to Telegram HTML
8. Sends reply with `ParseMode.Html`
9. Falls back to plain text if HTML parse fails

## Configuration

Via `appsettings.json` / environment variables:

```json
{
  "Telegram": {
    "BotToken": "123:abc",
    "AllowedUserIds": ["123456789"],
    "Mode": "Polling",
    "WebhookUrl": "https://example.com/api/telegram/webhook"
  }
}
```

- `BotToken`: Required. Env var: `Telegram__BotToken`
- `AllowedUserIds`: Required (empty = all blocked). Env var: `Telegram__AllowedUserIds__0`, etc.
- `Mode`: `Polling` (default) or `Webhook`
- `WebhookUrl`: Required when mode is `Webhook`

## Startup Modes

**Polling (default/local dev):**
- `TelegramBotService` implements `IHostedService`
- Calls Telegram.Bot's `ReceiveAsync()` in background loop
- No public URL needed

**Webhook (production):**
- `POST /api/telegram/webhook` endpoint registered via `MapTelegramWebhookEndpoints()`
- On startup: calls `SetWebhookAsync()` with configured URL + secret token
- On shutdown: calls `DeleteWebhookAsync()`
- Webhook validates secret token header (not API key auth)

**Missing token behavior:**
- `Mode=Webhook` + no token: throw on startup — you explicitly asked for Telegram, this is a broken deploy
- `Mode=Polling` + no token: log a warning and skip startup — may be intentional in dev environments where only some channels are active

## Update Filtering & Access Control

Updates are filtered early and explicitly:

1. **Update filter**: Only process updates where `Update.Message` is non-null, `Message.Text` is non-null, and `Chat.Type == ChatType.Private`. All other update types (edits, callbacks, group messages, media-only) are ignored in Phase 1.
2. **Access control**: After filtering, `TelegramAccessControl` checks `Message.From.Id` against `TelegramOptions.AllowedUserIds`. Empty list = all users blocked (secure by default). Unauthorized messages silently ignored.

## Markdown to Telegram HTML

Uses **Markdig** to parse markdown into an AST, then renders to Telegram's HTML subset via a custom renderer. No regex — Markdig handles nested markup, code fences, and edge cases correctly.

| Markdown | Telegram HTML |
|----------|--------------|
| `**bold**` | `<b>bold</b>` |
| `*italic*` | `<i>italic</i>` |
| `` `code` `` | `<code>code</code>` |
| ` ```block``` ` | `<pre><code>block</code></pre>` |
| `[text](url)` | `<a href="url">text</a>` |
| `~~strike~~` | `<s>strike</s>` |

Text content is HTML-escaped (`<`, `>`, `&`) during rendering. URLs in links are sanitized (only `http`/`https` schemes). Falls back to plain text on Telegram parse errors.

**Chunking**: Split the markdown at natural boundaries (paragraph breaks) *before* converting to HTML. Each chunk is converted independently, ensuring self-contained HTML. Max 4096 chars per markdown chunk. Note: HTML conversion adds tags so the final HTML may exceed 4096 — in practice LLM responses rarely hit this limit, and Telegram is lenient. Phase 2 can add post-conversion length validation if needed.

## Error Handling

- **LLM failure**: Reply with short error message
- **HTML parse failure**: Retry with plain text (strip tags)
- **Message too long**: Chunked before conversion (see Markdown section)
- **Telegram send failure**: Bounded retry — 3 attempts, exponential backoff (1s, 2s, 4s), only on transient failures (network errors, 429, 5xx). Give up after 3 attempts and log. Never crash the polling loop.

## Host Integration

```csharp
// Program.cs
builder.Services.AddTelegramChannel(builder.Configuration);
// ...
app.MapTelegramWebhookEndpoints();
```

## Conversation Identity

- ID: `telegram-{chatId}`
- Source: `"telegram"`
- Type: `ConversationType.Text`

## Dependencies

- `Telegram.Bot` NuGet package
- `Markdig` NuGet package (markdown parsing)

## Future Phases

- **Phase 2**: Groups, mention detection, media (photos/documents), per-group sessions, bot commands (`/reset`)
- **Phase 3**: Inline keyboards, reactions, streaming (edit-based), multi-account, forum topics
