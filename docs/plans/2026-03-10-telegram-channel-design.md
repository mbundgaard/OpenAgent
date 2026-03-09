# Telegram Channel — Design

## Overview

Add Telegram as the first inbound channel for OpenAgent. Introduces `IChannelProvider` contract and a self-contained `OpenAgent.Channel.Telegram` project.

Phase 1 scope: DM text messages, allowlist access control, polling + webhook modes, typing indicator. No streaming, groups, media, or bot commands.

## IChannelProvider Contract

Lives in `OpenAgent.Contracts`:

```csharp
public interface IChannelProvider : IConfigurable
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

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

**Graceful no-op:** If `BotToken` is not configured, `TelegramBotService.StartAsync` exits early. No crash, no error.

## Access Control

- `TelegramAccessControl` checks `Update.Message.From.Id` against `TelegramOptions.AllowedUserIds`
- Empty list = all users blocked (secure by default)
- Unauthorized messages silently ignored

## Markdown to Telegram HTML

Simple regex-based converter:

| Markdown | Telegram HTML |
|----------|--------------|
| `**bold**` | `<b>bold</b>` |
| `*italic*` | `<i>italic</i>` |
| `` `code` `` | `<code>code</code>` |
| ` ```block``` ` | `<pre><code>block</code></pre>` |
| `[text](url)` | `<a href="url">text</a>` |
| `~~strike~~` | `<s>strike</s>` |

Escapes `<`, `>`, `&` before applying tags. Falls back to plain text on Telegram parse errors.

Messages exceeding 4096 chars are chunked into multiple messages.

## Error Handling

- **LLM failure**: Reply with short error message
- **Telegram API failure**: Log and swallow (don't crash polling loop)
- **HTML parse failure**: Retry with plain text
- **Message too long**: Chunk at 4096 chars
- No retry/backoff in Phase 1

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

## Future Phases

- **Phase 2**: Groups, mention detection, media (photos/documents), per-group sessions, bot commands (`/reset`)
- **Phase 3**: Inline keyboards, reactions, streaming (edit-based), multi-account, forum topics
