# OpenClaw Telegram Channel — Reference Implementation

This document describes how OpenClaw implements Telegram integration. This serves as a reference for implementing a similar channel in Open Agent.

## Overview

OpenClaw uses the **grammY** library for Telegram Bot API integration. It supports both long-polling (default) and webhook modes.

**Key characteristics:**
- Long-polling by default (no public URL required)
- Webhook optional for production deployments
- DMs share the main agent session
- Groups get isolated sessions per chat
- Forum topics get isolated sessions per topic
- HTML formatting for messages
- Inline keyboards, reactions, stickers, media support

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     OpenClaw Gateway                         │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐   ┌─────────────┐   ┌─────────────┐       │
│  │  Telegram   │   │   Discord   │   │    Slack    │  ...  │
│  │  Channel    │   │   Channel   │   │   Channel   │       │
│  └──────┬──────┘   └─────────────┘   └─────────────┘       │
│         │                                                    │
│         ▼                                                    │
│  ┌─────────────────────────────────────────────────────┐   │
│  │              Shared Channel Envelope                 │   │
│  │  (normalized message format for all channels)        │   │
│  └──────────────────────┬──────────────────────────────┘   │
│                         │                                    │
│                         ▼                                    │
│  ┌─────────────────────────────────────────────────────┐   │
│  │                   Session Router                     │   │
│  │  DM → main session                                   │   │
│  │  Group → agent:main:telegram:group:<chatId>          │   │
│  │  Topic → agent:main:telegram:group:<chatId>:topic:<t>│   │
│  └──────────────────────┬──────────────────────────────┘   │
│                         │                                    │
│                         ▼                                    │
│  ┌─────────────────────────────────────────────────────┐   │
│  │                    Agent Loop                        │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

## Library: grammY

OpenClaw uses [grammY](https://grammy.dev/) — a modern Telegram Bot API framework for Node.js/Deno.

**Why grammY:**
- TypeScript-first
- Middleware-based architecture
- Built-in support for long-polling and webhooks
- Handles rate limiting and retries
- Active maintenance

**Key grammY concepts:**
```typescript
import { Bot } from "grammy";

const bot = new Bot("BOT_TOKEN");

// Middleware for all messages
bot.on("message", async (ctx) => {
  // ctx.message contains the Telegram message
  // ctx.reply() sends a response
  await ctx.reply("Hello!");
});

// Long-polling (default)
bot.start();

// Or webhook
// app.post("/webhook", webhookCallback(bot, "express"));
```

## Token Resolution

OpenClaw supports multiple ways to configure the bot token:

**Priority order:**
1. Per-account `tokenFile` (`channels.telegram.accounts.<id>.tokenFile`)
2. Per-account `botToken` (`channels.telegram.accounts.<id>.botToken`)
3. Global `tokenFile` (`channels.telegram.tokenFile`)
4. Global `botToken` (`channels.telegram.botToken`)
5. Environment variable (`TELEGRAM_BOT_TOKEN`)

**Token file format:**
Plain text file containing just the token (whitespace trimmed).

## Multi-Account Support

OpenClaw supports multiple Telegram bot accounts:

```json5
{
  channels: {
    telegram: {
      enabled: true,
      accounts: {
        main: {
          botToken: "123:abc",
          name: "Main Bot"
        },
        support: {
          botToken: "456:def",
          name: "Support Bot"
        }
      }
    }
  }
}
```

Each account can have its own:
- Token
- DM policy
- Group settings
- Capabilities

## Session Routing

**DM messages:**
- Route to main agent session
- Session key: `agent:<agentId>` (default session)

**Group messages:**
- Route to isolated group session
- Session key: `agent:<agentId>:telegram:group:<chatId>`
- Each group is independent

**Forum topic messages:**
- Route to isolated topic session
- Session key: `agent:<agentId>:telegram:group:<chatId>:topic:<threadId>`
- Each topic within a forum is independent

## Access Control

### DM Policy (`dmPolicy`)

| Mode | Behavior |
|------|----------|
| `pairing` | Unknown users get a pairing code; must be approved |
| `allowlist` | Only users in `allowFrom` can message |
| `open` | Anyone can DM (requires `allowFrom: ["*"]`) |
| `disabled` | DMs disabled |

### Group Policy (`groupPolicy`)

| Mode | Behavior |
|------|----------|
| `allowlist` | Only senders in `groupAllowFrom` can message |
| `open` | All group members can message |
| `disabled` | Group messages ignored |

### Group Allowlist (`groups`)

When `channels.telegram.groups` is set, it acts as an allowlist:
- Only listed groups (or `"*"`) are accepted
- Unlisted groups are ignored

```json5
{
  channels: {
    telegram: {
      groups: {
        "-1001234567890": {
          requireMention: false,
          groupPolicy: "open"
        },
        "*": {
          requireMention: true
        }
      }
    }
  }
}
```

## Mention Handling

By default, bots only respond to @mentions in groups.

**Detection methods:**
1. Native `@botname` mention
2. Custom patterns via `mentionPatterns`

**Configuration:**
```json5
{
  agents: {
    defaults: {
      groupChat: {
        mentionPatterns: ["@assistant", "hey bot"]
      }
    }
  }
}
```

**Per-group override:**
```json5
{
  channels: {
    telegram: {
      groups: {
        "-1001234567890": {
          requireMention: false  // Always respond
        }
      }
    }
  }
}
```

## Message Handling

### Inbound Processing

1. Receive update from Telegram (polling or webhook)
2. Normalize to shared envelope format:
   ```typescript
   interface ChannelMessage {
     provider: "telegram";
     chatId: string;
     messageId: string;
     senderId: string;
     senderName: string;
     text: string;
     replyToMessageId?: string;
     threadId?: number;
     media?: MediaAttachment[];
     // ... etc
   }
   ```
3. Check access control (dmPolicy/groupPolicy)
4. Check mention requirement (groups)
5. Route to appropriate session
6. Enqueue for agent processing

### Outbound Processing

1. Agent produces response
2. Convert markdown to Telegram HTML
3. Chunk if exceeds limit (4000 chars default)
4. Send via Bot API
5. Handle errors (retry on transient failures)

## Message Formatting

Telegram uses a subset of HTML for formatting:

```html
<b>bold</b>
<i>italic</i>
<u>underline</u>
<s>strikethrough</s>
<code>inline code</code>
<pre>code block</pre>
<a href="url">link</a>
```

**OpenClaw's conversion:**
- Markdown-ish input → Telegram HTML
- Raw HTML from models is escaped
- Falls back to plain text if HTML parsing fails

## Media Handling

### Inbound Media

Supported types:
- Photos
- Documents (files)
- Voice notes
- Video notes
- Stickers (static only)

Processing:
1. Download file via Bot API
2. Convert to appropriate format
3. Include in message as media placeholder or process through vision

### Outbound Media

```typescript
// Send photo
await ctx.replyWithPhoto(source, { caption: "..." });

// Send document
await ctx.replyWithDocument(source, { caption: "..." });

// Send voice note
await ctx.replyWithVoice(source);

// Send sticker
await ctx.replyWithSticker(fileId);
```

## Inline Keyboards

Send interactive buttons with messages:

```json5
{
  action: "send",
  channel: "telegram",
  to: "123456789",
  message: "Choose:",
  buttons: [
    [
      { text: "Yes", callback_data: "yes" },
      { text: "No", callback_data: "no" }
    ]
  ]
}
```

**Callback handling:**
When user clicks, callback data is sent to agent as: `callback_data: <value>`

**Configuration:**
```json5
{
  channels: {
    telegram: {
      capabilities: {
        inlineButtons: "allowlist"  // off|dm|group|all|allowlist
      }
    }
  }
}
```

## Reactions

**Receiving reactions:**
- Telegram sends `message_reaction` events
- Converted to system events for agent context

**Sending reactions:**
```json5
{
  action: "react",
  channel: "telegram",
  chatId: "123456789",
  messageId: "42",
  emoji: "👍"
}
```

**Configuration:**
```json5
{
  channels: {
    telegram: {
      reactionNotifications: "own",  // off|own|all
      reactionLevel: "minimal"       // off|ack|minimal|extensive
    }
  }
}
```

## Streaming (Draft Messages)

Telegram supports streaming via draft messages in DMs:

**Requirements:**
- Forum topic mode enabled for bot (BotFather)
- Private chat with thread
- `streamMode` not `off`

**Modes:**
- `partial`: Update draft with latest streaming text
- `block`: Update in larger chunks
- `off`: Disabled

```json5
{
  channels: {
    telegram: {
      streamMode: "partial"
    }
  }
}
```

## Long-Polling vs Webhook

### Long-Polling (Default)

```typescript
// grammY handles this internally
bot.start();
```

**Pros:**
- No public URL needed
- Works behind NAT/firewall
- Simple setup

**Cons:**
- Slightly higher latency
- Constant connection to Telegram

### Webhook

```json5
{
  channels: {
    telegram: {
      webhookUrl: "https://example.com/telegram-webhook",
      webhookSecret: "random-secret-string",
      webhookPath: "/telegram-webhook"  // local path
    }
  }
}
```

**Pros:**
- Lower latency
- More efficient at scale

**Cons:**
- Requires public HTTPS URL
- More complex setup

## Error Handling

### Retry Policy

```json5
{
  channels: {
    telegram: {
      retry: {
        attempts: 3,
        minDelayMs: 1000,
        maxDelayMs: 30000,
        jitter: true
      }
    }
  }
}
```

Retries on:
- Network errors
- 429 (rate limit)
- 5xx server errors

### Fallback

If HTML parsing fails, retry with plain text.

## Commands

### Native Commands

OpenClaw registers commands with Telegram's menu:
- `/status` — session status
- `/reset` — reset conversation
- `/model` — change model

### Custom Commands

```json5
{
  channels: {
    telegram: {
      customCommands: [
        { command: "backup", description: "Git backup" }
      ]
    }
  }
}
```

## Configuration Reference

```json5
{
  channels: {
    telegram: {
      // Core
      enabled: true,
      botToken: "123:abc",
      tokenFile: "/path/to/token",
      
      // Access control
      dmPolicy: "pairing",          // pairing|allowlist|open|disabled
      allowFrom: ["123456789"],     // user IDs or @usernames
      groupPolicy: "allowlist",     // allowlist|open|disabled
      groupAllowFrom: ["123456789"],
      
      // Groups
      groups: {
        "-1001234567890": {
          requireMention: false,
          groupPolicy: "open",
          skills: ["skill1"],       // filter skills
          systemPrompt: "Extra prompt",
          enabled: true
        },
        "*": {
          requireMention: true
        }
      },
      
      // Formatting
      textChunkLimit: 4000,
      chunkMode: "length",          // length|newline
      linkPreview: true,
      
      // Media
      mediaMaxMb: 5,
      
      // Streaming
      streamMode: "partial",        // off|partial|block
      
      // Webhook (optional)
      webhookUrl: "https://...",
      webhookSecret: "...",
      webhookPath: "/telegram-webhook",
      
      // Features
      capabilities: {
        inlineButtons: "allowlist"
      },
      reactionNotifications: "own",
      reactionLevel: "minimal",
      
      // Timeouts
      timeoutSeconds: 500,
      
      // Multi-account
      accounts: {
        main: { botToken: "...", name: "Main" }
      }
    }
  }
}
```

## Implementation Notes for Open Agent

### Recommended Approach

1. **Use grammY** — same library OpenClaw uses, well-documented
2. **Start with long-polling** — simpler, no infrastructure needed
3. **Implement core flow first:**
   - Receive message
   - Route to conversation
   - Send response

### Minimal Implementation

```typescript
import { Bot } from "grammy";

interface TelegramChannelConfig {
  botToken: string;
  allowedUsers?: string[];  // User IDs
}

export function createTelegramChannel(
  config: TelegramChannelConfig,
  onMessage: (msg: InboundMessage) => Promise<string>
) {
  const bot = new Bot(config.botToken);
  
  bot.on("message:text", async (ctx) => {
    const userId = String(ctx.from?.id);
    
    // Access control
    if (config.allowedUsers && !config.allowedUsers.includes(userId)) {
      return; // Ignore unauthorized
    }
    
    // Build inbound message
    const inbound: InboundMessage = {
      provider: "telegram",
      chatId: String(ctx.chat.id),
      messageId: String(ctx.message.message_id),
      senderId: userId,
      senderName: ctx.from?.first_name ?? "Unknown",
      text: ctx.message.text,
    };
    
    // Process and respond
    const response = await onMessage(inbound);
    await ctx.reply(response, { parse_mode: "HTML" });
  });
  
  return {
    start: () => bot.start(),
    stop: () => bot.stop(),
  };
}
```

### Phase 1: Basic DM Support

- Bot token config
- Long-polling
- Text messages only
- Single session routing
- Basic access control

### Phase 2: Groups + Media

- Group message handling
- Mention detection
- Photo/document support
- Per-group sessions

### Phase 3: Advanced Features

- Inline keyboards
- Reactions
- Streaming
- Multi-account
- Webhook support

### Key Dependencies

```json
{
  "dependencies": {
    "grammy": "^1.x"
  }
}
```

## Testing

### Local Testing

1. Create bot via @BotFather
2. Set token in config
3. Start gateway
4. DM the bot

### Bot Privacy (Groups)

To receive all group messages:
- Disable privacy mode via @BotFather (`/setprivacy`)
- Or make bot an admin

### Getting IDs

- User ID: DM @userinfobot or check logs
- Group ID: Forward message to @userinfobot (negative number)
