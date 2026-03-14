# Telegram Integration Review

**Date:** 2026-03-14
**Scope:** All files in `OpenAgent.Channel.Telegram`, related tests, host wiring, and webhook endpoints.

## Overall

Clean, well-structured integration across 10 source files with clear separation of concerns. Supports both polling and webhook modes, streaming (sendMessageDraft) and batch response delivery, thinking output, reply-to tracking, and markdown-to-HTML conversion.

## Items to Address

### 1. Webhook endpoint AllowAnonymous

**File:** `TelegramWebhookEndpoints.cs:67`

The endpoint is `AllowAnonymous` because Telegram can't send auth headers. Protection comes from the secret token validation (constant-time comparison). This is correct but should have a comment explaining why it's anonymous.

**Status:** [x] Done — added inline comment on AllowAnonymous

### 2. New sender per polling update

**File:** `TelegramChannelProvider.cs:142`

`HandlePollingUpdateAsync` creates a new `TelegramBotClientSender` for every update. The sender is stateless so this works, but it could be cached as a field since the bot client doesn't change.

**Status:** [x] Done — sender cached as field, created once at startup

### 3. Batch mode ignores tool events

**File:** `TelegramMessageHandler.cs:337-340`

`CollectAndSendAsync` only collects `TextDelta` and `AssistantMessageSaved`. Tool calls and results are silently dropped — no thinking output in batch mode. This may be intentional (batch = simple mode) but should be a deliberate decision.

**Decision needed:** Should batch mode also support thinking output?

**Status:** [x] Done — batch mode now sends thinking messages when showThinking is enabled

### 4. Fire-and-forget in webhook

**File:** `TelegramWebhookEndpoints.cs:53`

`_ = Task.Run(...)` processes the update after returning 200 OK to Telegram. If the handler crashes, only the log captures it. This is the correct pattern for webhooks (Telegram retries on non-200), but worth noting for awareness.

**Status:** [ ] No action needed — correct pattern, noted for awareness

### 5. TelegramBotClientSender HttpClient lifecycle

**File:** `TelegramBotClientSender.cs:23`

The sender creates an `HttpClient` in its constructor but doesn't implement `IDisposable`. In polling mode (item #2), a new sender is created per update, which means a new `HttpClient` per update — minor socket leak concern. Caching the sender (item #2) would resolve this as well.

**Status:** [x] Resolved with #2 — single sender, single HttpClient
