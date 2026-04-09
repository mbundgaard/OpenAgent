# Telegram Integration Summary

## 1. Entry Points

Two inbound modes are supported, both in `src/telegram/`:

### Webhook Mode (`src/telegram/webhook.ts`)
- HTTP server binds to configurable host/port (default `127.0.0.1:8787`)
- Route: `POST /telegram-webhook` (configurable via `webhookPath`)
- Uses grammY's `webhookCallback()` for secure request handling
- Secret token validation via `x-telegram-bot-api-secret-token` header
- Calls `bot.api.setWebhook()` to register with Telegram's servers

### Long Polling Mode (`src/telegram/polling-session.ts`) — Default
- Implemented with grammY's `run()` runner with per-chat sequencing
- Uses `getUpdates` Telegram Bot API call with configurable timeout
- Watchdog detects polling stalls (>90s) and auto-restarts
- Persists update IDs to avoid duplicates across restarts
- Graceful backoff on network errors (2–30 seconds exponential)

---

## 2. Message Routing & Session Keys

**Key files:**
- `src/telegram/conversation-route.ts` — route resolution + session key building
- `src/telegram/bot-message-context.session.ts` — session context
- `src/routing/session-key.ts` — generic session key builder
- `src/routing/resolve-route.ts` — agent routing logic

### Session Key Format

Built by `buildAgentSessionKey()`:

```
agent:<agentId>:telegram:<peerId>
```

**Peer ID patterns:**

| Context | Peer ID | Full Session Key Example |
|---|---|---|
| DM (direct) | `direct:<userId>` | `agent:main:telegram:direct:12345` |
| DM with thread | `direct:<userId>` + threadId | `agent:main:telegram:direct:12345:thread:999` |
| Group | `group:<groupId>` | `agent:main:telegram:group:-1001234567890` |
| Forum topic | `group:<groupId>:topic:<topicId>` | `agent:main:telegram:group:-1001234567890:topic:42` |

### Routing Resolution (`resolveTelegramConversationRoute()`)

1. Resolve agent via `resolveAgentRoute()` using channel + peer kind + peer ID
2. Check for **topic-level agent override** (`agentId` in `channels.telegram.groups.<chatId>.topics.<threadId>`)
3. Check for **ACP persistent bindings** (dedicated ACP harness pinned to forum topics)
4. Check for **dynamic thread bindings** (runtime `/acp spawn` bindings)
5. Fall back to default agent for channel

---

## 3. Channel Abstraction — `ChannelPlugin` Interface

**Key file:** `extensions/telegram/src/channel.ts`

Telegram is implemented as a **ChannelPlugin** alongside WhatsApp, Discord, Slack, etc.

### Plugin Definition

```typescript
// extensions/telegram/src/channel.ts
export const telegramPlugin: ChannelPlugin<ResolvedTelegramAccount, TelegramProbe> = {
  id: "telegram",
  meta: { ... },
  onboarding: telegramOnboardingAdapter,
  pairing: { ... },
  capabilities: {
    chatTypes: ["direct", "group", "channel", "thread"],
    reactions: true,
    threads: true,
    media: true,
    polls: true,
    nativeCommands: true,
    blockStreaming: true,
  },
  config: { ... },
  security: { ... },
  gateway: {
    startAccount: async (ctx) => { /* Start polling/webhook */ },
    logoutAccount: async ({ accountId, cfg }) => { ... },
  },
  outbound: { /* sendText, sendMedia, sendPayload */ },
  messageActions: { /* telegram-specific actions */ },
}
```

### Generic Interfaces Implemented

- `ChannelConfigAdapter` — token resolution, account config
- `ChannelSecurityAdapter` — DM/group policy enforcement
- `ChannelOutboundAdapter` — message delivery
- `ChannelMessageActionAdapter` — send, react, delete, edit, sticker, topic-create
- `ChannelGatewayAdapter` — startup/shutdown
- `ChannelPairingAdapter` — DM approval notifications

**Plugin types:** `src/channels/plugins/types.plugin.ts`

---

## 4. Outbound — Sending Replies to Telegram

**Key files:**
- `src/telegram/send.ts` — `sendMessageTelegram()` function
- `src/channels/plugins/outbound/telegram.ts` — `telegramOutbound` adapter

### Outbound Adapter

```typescript
export const telegramOutbound: ChannelOutboundAdapter = {
  deliveryMode: "direct",
  chunker: markdownToTelegramHtmlChunks,
  textChunkLimit: 4000,
  sendText: async ({ cfg, to, text, accountId, deps, replyToId, threadId }) => { ... },
  sendMedia: async ({ ... }) => { ... },
  sendPayload: async ({ ... }) => { ... },
}
```

### Features

- HTML formatting with Markdown-to-HTML conversion
- Fallback to plain text if HTML parse fails
- Thread-aware sends (DM topics + forum topics via `message_thread_id`)
- Reply threading with `reply_to_message_id` and `quote_text`
- Inline buttons (keyboard markup) with scope gating (dm/group/all)
- Streaming edits via `editMessageText` for live previews
- Media handling: photos, audio, voice notes, video notes, documents, stickers
- Retry logic for network/API errors

### Telegram Bot API Calls Used

| Method | Purpose |
|---|---|
| `api.sendMessage()` | Text messages |
| `api.sendPhoto()`, `api.sendVideo()`, `api.sendAudio()`, etc. | Media |
| `api.editMessageText()` | Streaming edits |
| `api.setMessageReaction()` | Reactions |
| `api.deleteMessage()` | Message deletion |
| `api.createForumTopic()` | Forum topic creation |

---

## 5. Configuration

### Environment Variables (`.env.example`)

```bash
TELEGRAM_BOT_TOKEN=123456:ABCDEF...
```

### Config Structure (`src/config/types.telegram.ts`)

```typescript
channels: {
  telegram: {
    enabled: true,
    botToken: "123456:ABC",
    tokenFile: "/path/to/token",
    dmPolicy: "pairing" | "allowlist" | "open" | "disabled",
    allowFrom: [123, 456, "telegram:789"],
    groupPolicy: "open" | "allowlist" | "disabled",
    groupAllowFrom: [123, 456],
    groups: {
      "-1001234567890": {
        requireMention: true,
        groupPolicy: "allowlist",
        topics: {
          "42": {
            agentId: "coder",
            requireMention: false,
          },
        },
      },
    },
    webhookUrl: "https://example.com/telegram-webhook",
    webhookSecret: "secret-token",
    webhookPort: 8787,
    webhookHost: "127.0.0.1",
    actions: {
      sendMessage: true,
      reactions: true,
      deleteMessage: true,
      editMessage: true,
      sticker: false,
      poll: true,
    },
    replyToMode: "off" | "first" | "all",
    streaming: "off" | "partial" | "block" | "progress",
    textChunkLimit: 4000,
    mediaMaxMb: 100,
    ackReaction: "eyes",
    proxy: "socks5://user:pass@host:1080",
    accounts: {
      default: { botToken: "..." },
      alerts: { botToken: "...", dmPolicy: "open" },
    },
  },
}
```

### Documentation

- `docs/channels/telegram.md` — user-facing docs

---

## 6. Plugin/Module Structure

Telegram is a **self-contained plugin** with core logic in `src/telegram/` and a plugin wrapper in `extensions/telegram/`.

### Directory Layout

```
src/telegram/                          # Core logic (41 TypeScript files)
  ├── bot.ts                           # Bot creation & config
  ├── bot-handlers.ts                  # Message/update handlers (53KB, largest file)
  ├── bot-message-context.ts           # Context building (16KB)
  ├── bot-message-context.session.ts   # Session context
  ├── bot-message-dispatch.ts          # Inbound routing & dispatch (29KB)
  ├── webhook.ts                       # HTTP webhook server
  ├── polling-session.ts               # Long polling loop
  ├── send.ts                          # Telegram API send
  ├── sequential-key.ts               # Sequential processing keys
  ├── conversation-route.ts            # Agent/session routing
  ├── accounts.ts                      # Multi-account support
  ├── allowed-updates.ts               # Filter which update types to receive
  ├── format.ts                        # Markdown to HTML formatting
  ├── targets.ts                       # Chat ID normalization
  ├── button-types.ts                  # Inline keyboard buttons
  ├── group-config-helpers.ts          # Group-specific config
  ├── group-access.ts                  # Group message authorization
  ├── dm-access.ts                     # DM message authorization
  ├── inline-buttons.ts               # Button scope gating
  ├── thread-bindings.ts              # Dynamic ACP thread binding
  ├── model-buttons.ts                # Model selection UI
  ├── bot/
  │   ├── helpers.ts                   # Peer ID builders, thread ID resolution
  │   ├── types.ts                     # grammY context types
  │   └── delivery.*.ts               # Media loading & retry
  └── *.test.ts                        # 50+ unit/e2e tests

extensions/telegram/                   # Plugin wrapper
  ├── index.ts                         # Plugin entry point
  └── src/
      ├── channel.ts                   # telegramPlugin definition (557 lines)
      ├── runtime.ts                   # Runtime store for TelegramRuntime
      └── channel.test.ts             # Plugin tests

src/channels/plugins/                  # Shared plugin adapters
  ├── outbound/telegram.ts             # Outbound adapter
  ├── actions/telegram.ts              # Message actions
  ├── normalize/telegram.ts            # Target normalization
  └── onboarding/telegram.ts           # Setup wizard

src/agents/tools/telegram-actions.ts   # Agent tool handlers
src/config/types.telegram.ts           # Type definitions
```

### Integration Points

1. **Bot library:** grammY (`grammy`, `@grammyjs/types`)
2. **Runtime injection:** `extensions/telegram/src/runtime.ts` injects via plugin SDK
3. **Config loading:** Uses shared `src/config/types.telegram.ts`
4. **Routing:** Uses `src/routing/resolve-route.ts` for agent dispatch
5. **Outbound:** Registered in channel plugin registry, calls via `sendMessageTelegram()`
6. **Session storage:** Uses `src/config/sessions.ts` for state

---

## 7. Key Architectural Patterns

### Inbound Flow

```
Telegram Update
  → grammY Bot (webhook or polling)
  → sequentialize() — per-chat/per-thread ordering
  → getTelegramSequentialKey() — compute sequential key
  → buildTelegramInboundContextPayload() — normalize & build context
  → resolveTelegramConversationRoute() — resolve agent + session key
  → gateway.enqueueMessage() — dispatch to agent session
```

### Outbound Flow

```
Agent reply (text/media/payload)
  → telegramOutbound.sendText/Media/Payload()
  → markdownToTelegramHtmlChunks() — chunk at 4000 chars
  → sendMessageTelegram() — call Bot API
  → Retry on recoverable errors
  → Fallback to plain text on HTML parse errors
```

### Multi-Account Support

- Accounts indexed by ID (e.g., `"default"`, `"alerts"`, `"ops"`)
- Each account has own bot token, config, and outbound session
- Group/topic-level agent overrides route specific conversations to different agents

### Security Model

- **DM policy:** pairing-based allowlist (default), explicit allowlist, or open
- **Group policy:** per-group/per-topic sender authorization
- **Access checks:** `isSenderAllowed()` validates before processing

---

## Deep Dive: Implementation Patterns Worth Knowing

### 8. Streaming / Progressive Edits

**File:** `src/telegram/draft-stream.ts`

- **Default throttle:** 1000ms between edits (configurable, minimum 250ms)
- **Two transport modes:**
  - **Draft transport** (`sendMessageDraft`): Telegram's experimental draft API for smoother streaming
  - **Message transport** (fallback): `sendMessage` + `editMessageText` if draft unavailable
- **First preview delay:** Minimum 30 chars accumulated before sending first preview — improves push notification UX (users see meaningful text, not a single word)
- **Thread fallback:** Automatically retries without `message_thread_id` if a forum topic send fails ("thread not found")
- **Superseded preview handling:** Tracks when `forceNewMessage()` switches generations to clean up stale streaming messages
- **Max size:** Capped at 4096 characters (Telegram's hard limit)
- **Materialization:** Draft previews can be converted to permanent messages with `materialize()`

---

### 9. Rate Limiting & Retry Logic

**Files:** `src/telegram/sendchataction-401-backoff.ts`, `src/telegram/network-errors.ts`

#### 401 (Unauthorized) — Circuit Breaker Pattern

- Exponential backoff: 1s → 2s → 4s → ... → 5 minutes (300s max)
- Jitter: 10%
- After **10 consecutive 401s**: suspend all `sendChatAction` calls permanently (prevents Telegram from deleting the bot)
- Single shared handler across all message contexts
- Counter resets on success

#### Network Error Classification

| Category | Codes | Retry? |
|---|---|---|
| Pre-connect (safe to retry on send) | ECONNREFUSED, ENOTFOUND, EAI_AGAIN, ENETUNREACH, EHOSTUNREACH | Yes |
| Recoverable (polling/idempotent only) | ECONNRESET, EPIPE, ETIMEDOUT, ECONNABORTED | Idempotent only |

`isSafeToRetrySendError()` only retries errors that occurred *before* reaching Telegram servers — prevents duplicate message sends.

#### Media Download Retry

- 3 attempts for `getFile()`
- Delay: 1000–4000ms with 20% jitter
- "File too big" (>20MB) errors are NOT retried (permanent failure)

---

### 10. Markdown to Telegram HTML Conversion

**File:** `src/telegram/format.ts`

Key edge cases handled:

- **Auto-linkified file references:** Detects markdown-it accidentally converting `README.md` → `http://README.md` and wraps in `<code>` tags instead
- **File extensions with TLD overlap:** Maintains an allowlist (`md`, `go`, `py`, `pl`, `sh`, `am`, `at`, `be`, `cc`) that share TLDs with country codes
- **Spoilers:** Uses `<tg-spoiler>` tags
- **Blockquotes:** Uses `<blockquote>` tags
- **Chunking:** Proportional splitting based on rendered HTML length ratio; fallback to 50% split if proportional calculation fails. Respects the 4096-char HTML limit (not text limit).
- **Parse failure recovery:** Reverts to plain text if HTML markup fails parsing

---

### 11. Media Handling

**Files:** `src/telegram/bot/delivery.resolve-media.ts`, `src/telegram/bot-handlers.ts`

#### Inbound

- **Download idle timeout:** 30 seconds
- **SSRF policy:** Trusts `api.telegram.org` even in restricted networks
- **File size limit:** 20MB (Telegram Bot API limit) — returns placeholder on "file is too big"
- **Sticker handling:** Only processes static WEBP stickers; skips animated (TGS) and video (WEBM)
- **Sticker caching:** Persistent cache of `file_unique_id` → description to avoid re-processing
- **Placeholder fallback:** Returns `<media:audio>`, `<media:sticker>`, etc. if download fails

#### Outbound

- **Media groups:** Buffers multi-image messages with 500ms timeout (configurable via `testTimings.mediaGroupFlushMs`)

---

### 12. Text Fragment Reassembly

**File:** `src/telegram/bot-handlers.ts` (lines ~881–943)

Telegram splits long user pastes into multiple messages (~4096 chars each). The integration reassembles them:

- **Trigger:** Message text >= 500 chars starts fragment buffering
- **Buffer key:** `text:${chatId}:${threadId}:${senderId}`
- **Max gap between fragments:** 5000ms
- **Max message ID gap:** 3
- **Max parts:** 10 messages
- **Max total:** 20,000 chars
- **Flushing:** Scheduled with timeout; cleared when gap exceeded or limits reached

This is important — without this, a long paste would trigger 4–5 separate agent responses.

---

### 13. Inline Keyboards & Callback Queries

**Files:** `src/telegram/button-types.ts`, `src/telegram/inline-buttons.ts`

- **Scope gating:** Buttons can be restricted to `off`, `dm`, `group`, `all`, or `allowlist`
- **Callback handling:** `answerCallbackQuery()` is called immediately to prevent Telegram retry timeouts, before processing the action
- **Button styles:** `danger`, `success`, `primary` (mapped to visual indicators)

---

### 14. Group Mention Detection

**Files:** `src/telegram/bot-message-context.body.ts`, `src/telegram/bot-message-context.implicit-mention.test.ts`

- **Explicit:** Detects `@botname` in text via Telegram entity parsing
- **Implicit:** When `requireMention=true` and user replies to a bot's own message — treated as an implicit mention (no `@` needed)
  - Exception: replies to forum service messages (`forum_topic_created`, etc.) are NOT implicit mentions
- **Audio preflight:** If a group message has audio-only with no text and requires mention, the bot transcribes the audio first, then checks for mention in the transcript before deciding to skip

---

### 15. Deduplication

**File:** `src/telegram/bot-updates.ts`

- **TTL:** 5 minutes (300s)
- **Max cache size:** 2000 entries
- **Key formats:** `update:${updateId}`, `callback:${callbackId}`, `message:${chatId}:${messageId}`
- **Watermark strategy:** Only persists an offset strictly less than the smallest pending (in-flight) update ID — prevents skipping updates that are still being processed when the process restarts

---

### 16. grammY Middleware Stack

**File:** `src/telegram/bot.ts` (lines ~163–279)

Middleware executes in this order:

1. **API throttler** — `apiThrottler()` from `@grammyjs/transformer-throttler`
2. **Global error catch** — prevents unhandled rejections from crashing the process
3. **Update tracking** — monitors `update_id`s entering/leaving pipeline, maintains watermark
4. **Sequentialization** — `sequentialize(getTelegramSequentialKey)` prevents concurrent processing per chat
   - Key: `telegram:${chatId}` or `telegram:${chatId}:topic:${topicId}`
   - Control commands get a separate key: `telegram:${chatId}:control`
5. **Raw update logging** (verbose mode only) — truncates large strings/arrays for safety
6. **Custom handlers** — message, callback, native commands, etc.

---

### 17. Command Registration

**File:** `src/telegram/bot-native-command-menu.ts`

- **Lazy sync via hashing:** Maintains SHA256 hash of command list in state dir; only calls `setMyCommands()` if hash changes
- **Limits:** Max 100 commands (Telegram API limit); overflow handled with 0.8 retry ratio (retries with 80% of set)
- **Validation:** Command names must be a-z, 0-9, underscore; max 32 chars; descriptions required
- **Conflict detection:** Plugin commands validated and checked for conflicts

---

### 18. Proxy Support

**File:** `src/infra/net/proxy-fetch.ts`

- Uses undici's `ProxyAgent` and `EnvHttpProxyAgent`
- Respects `HTTPS_PROXY`, `HTTP_PROXY` (and lowercase variants)
- Respects `NO_PROXY` / `no_proxy` exclusions
- Graceful fallback to direct fetch if proxy URL is malformed
- Passed as `fetch` option to grammY Bot client

---

### 19. Error Handling Philosophy

**File:** `src/telegram/send.ts`

| Error | Handling |
|---|---|
| `MESSAGE_NOT_MODIFIED` | Silently treated as success (no-op during streaming edits) |
| HTML parse errors | Falls back to plain text send automatically |
| Thread not found | Retries without `message_thread_id` (forum → general fallback) |
| Chat not found | Enhanced error with routing diagnosis info |
| User-facing errors | Generic "Something went wrong" |
| Internal errors | Full detailed messages with context to logs |

---

## Key Takeaways for Building a Telegram Integration

1. **Streaming needs throttling** — Don't edit faster than ~1s intervals or you'll hit rate limits. Consider draft API for smoother UX.
2. **Rate limiting is a circuit breaker problem** — Accumulate 401s and suspend operations; don't just retry blindly or Telegram may revoke your bot.
3. **Retry only pre-connect errors on sends** — If the request reached Telegram, retrying risks duplicate messages. Only retry connection-level failures.
4. **Fragment reassembly is essential** — Long user pastes arrive as multiple messages. Without buffering, you'll trigger multiple agent responses.
5. **Forum topics are pervasive** — Nearly every send/receive operation must check for and handle `message_thread_id`. The general topic (ID=1) is special — Telegram rejects it in API calls.
6. **Implicit mention is a UX win** — Requiring `@bot` in every group message is annoying. Detecting replies to bot messages as implicit mentions is a much better experience.
7. **Sticker/media downloads fail often** — Always have placeholder fallbacks. Animated stickers (TGS/WEBM) are complex formats — consider skipping them.
8. **Command sync should be lazy** — Hash the command list and only call `setMyCommands()` when it changes to avoid unnecessary API calls on every boot.
9. **Sequentialization prevents race conditions** — Process messages per-chat sequentially. Without this, concurrent messages in the same chat can cause out-of-order replies and state corruption.
10. **HTML is the safe Telegram formatting mode** — Telegram's MarkdownV2 is notoriously fragile. Convert to HTML and fall back to plain text on failure.
