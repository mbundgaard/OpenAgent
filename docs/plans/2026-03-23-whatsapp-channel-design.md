# WhatsApp Channel Design

## Summary

Add WhatsApp as a channel provider using Baileys (Node.js) running as a managed child process within the .NET host. Also fix conversation mapping for both Telegram and WhatsApp so each platform chat gets its own conversation.

## Architecture

### Node Child Process Bridge

WhatsApp Web has no usable .NET library. Baileys (@whiskeysockets/baileys) is the only production-grade implementation and it's Node.js. Rather than running a separate sidecar container, the .NET `WhatsAppChannelProvider` spawns and manages a Node child process directly.

Communication uses stdin/stdout JSON lines — no ports, no networking, process dies with parent.

```
[WhatsApp] <-> [Node child (Baileys)] <-> stdin/stdout <-> [.NET WhatsAppChannelProvider]
```

### Process Lifecycle

The Node process is NOT always running. Three states:

| State | Creds exist? | Node running? | QR endpoint returns |
|-------|-------------|---------------|---------------------|
| Unpaired | No | No | Starts Node, waits for QR, returns it |
| Pairing | No | Yes | Latest QR data |
| Connected | Yes | Yes | `{"status":"connected"}` |

- **App starts** -> ConnectionManager loads WhatsApp connections -> if creds exist in the connection's data directory, start Node process immediately.
- **No creds** -> don't start Node. Connection is "unpaired".
- **QR endpoint hit** -> starts Node process, waits for first QR event, returns it. Subsequent polls get the latest QR.
- **User scans QR** -> Node emits `connected`, Baileys writes creds to the auth directory -> normal message flow begins.
- **Next restart** -> creds exist -> connects automatically, no QR needed.

### Process Crashes and Reconnection

- Node exits with code 0 = clean shutdown (StopAsync requested via `{"type":"shutdown"}` on stdin).
- Node exits non-zero = crash -> .NET restarts with exponential backoff (2s -> 30s, max 10 attempts).
- If Node doesn't exit within 5s after shutdown command, force kill.
- **Backoff resets** after a successful connection held for >60s. A single crash after hours of uptime starts the sequence fresh.
- After exhausting retries, the provider enters a "failed" state and logs an error. It can be restarted manually via the connection API.

### Disconnection: loggedOut

When the Node process emits `{"type":"disconnected","reason":"loggedOut"}`, the user has unlinked the device from their phone. The provider must:

1. Kill the Node process.
2. Delete the auth directory (`{dataPath}/connections/whatsapp/{connectionId}/`).
3. Transition to "unpaired" state.
4. Do NOT retry — stale creds would just fail repeatedly.

The user must re-pair via the QR endpoint.

### Health Monitoring

The Node process sends a `{"type":"pong"}` in response to `{"type":"ping"}` on stdin. The .NET side sends a ping every 60s. If no pong is received within 10s, the process is considered hung and force-restarted.

## JSON Line Protocol

### Node -> .NET (stdout)

```jsonl
{"type":"qr","data":"2@AbC123..."}
{"type":"connected","jid":"+4512345678@s.whatsapp.net"}
{"type":"message","id":"ABCDEF","chatId":"+4599887766@s.whatsapp.net","from":"+4599887766","pushName":"Alice","text":"Hello","timestamp":1711180800}
{"type":"message","id":"GHIJKL","chatId":"120363xxx@g.us","from":"+4599887766","pushName":"Alice","text":"Hey group","timestamp":1711180801}
{"type":"disconnected","reason":"loggedOut"}
{"type":"pong"}
{"type":"error","message":"auth failed"}
```

### .NET -> Node (stdin)

```jsonl
{"type":"send","chatId":"+4599887766@s.whatsapp.net","text":"Hi Alice!"}
{"type":"composing","chatId":"+4599887766@s.whatsapp.net"}
{"type":"ping"}
{"type":"shutdown"}
```

### Stdin Write Serialization

Multiple .NET threads may write to stdin concurrently (send response, composing indicator, ping, shutdown). All stdin writes must be serialized through a `Channel<string>` with a single consumer that drains to the process stdin. Each write is a complete JSON line including the newline character.

## Per-Connection Data Directory

Baileys uses `useMultiFileAuthState(folder)` which manages multiple files (creds, session keys, pre-keys, etc.) in a directory. The provider passes the connection's data directory to the Node process.

Path: `{dataPath}/connections/whatsapp/{connectionId}/`

Generic pattern — other channels can use `{dataPath}/connections/{channelType}/{connectionId}/` for their own state.

## Message Deduplication

Baileys replays recent message history on reconnect, which can trigger duplicate LLM calls. The .NET side maintains an in-memory LRU cache of processed message IDs (max 5000 entries, 20-minute TTL). If an incoming message ID has already been processed, it is silently dropped.

## Conversation Mapping (Both Channels)

### Current behavior (Telegram)

All messages through a connection funnel into one conversation (connection's `ConversationId`). Incorrect for groups and multi-user scenarios.

### New behavior (Telegram + WhatsApp)

Each unique platform chat maps to its own conversation. Conversation ID derived from the chat:

| Platform | Chat ID source | ConversationId |
|----------|---------------|----------------|
| Telegram DM | `chat.Id` | `telegram:{connectionId}:{chatId}` |
| Telegram group | `chat.Id` | `telegram:{connectionId}:{chatId}` |
| WhatsApp DM | `chatId` from protocol | `whatsapp:{connectionId}:{chatId}` |
| WhatsApp group | `chatId` from protocol | `whatsapp:{connectionId}:{chatId}` |

The `chatId` in the JSON line protocol is always the chat identifier — sender JID for DMs, group JID for groups. Both DM and group rows use the same field from the protocol.

Uses `GetOrCreate` — new chat = new conversation, returning chat = existing conversation.

### Group Message Sender Attribution

Group messages include `from` and `pushName` fields. When storing a group message in the conversation, prefix the text with the sender name: `[Alice] Hey group`. This gives the LLM context about who said what in the conversation.

### Migration

Existing Telegram conversations used the connection's `ConversationId` (a GUID). After this change, new messages from the same chat will create a conversation with a derived ID (e.g., `telegram:{connectionId}:{chatId}`), orphaning the old one.

This is acceptable — Telegram is not yet in production use. No data migration needed. If this changes before implementation, a one-time migration script can rename existing conversation IDs.

### Connection.ConversationId field

The `ConversationId` property on `Connection` is no longer used by channel providers that derive per-chat IDs. Keep the field on the model for now, but channel providers ignore it in favor of derived IDs.

## Project Structure

```
OpenAgent.Channel.WhatsApp/
  WhatsAppChannelProvider.cs          IChannelProvider — spawns/manages Node process
  WhatsAppChannelProviderFactory.cs   Creates provider from Connection config
  WhatsAppMessageHandler.cs           Inbound processing, access control, LLM call, response
  WhatsAppAccessControl.cs            AllowedChatIds check (JID-based)
  WhatsAppMarkdownConverter.cs        Markdown -> WhatsApp formatting
  WhatsAppOptions.cs                  Config model
  WhatsAppNodeProcess.cs              Process lifecycle, stdin/stdout protocol
  WhatsAppWebhookEndpoints.cs         QR code endpoint
  node/
    package.json                      Baileys dependency (pinned exact version)
    baileys-bridge.js                 ~200-300 lines, the bridge script
    package-lock.json
```

## Access Control

Single `AllowedChatIds` list in config. Uses JID format consistently: `+4512345678@s.whatsapp.net` for DMs, `120363xxx@g.us` for groups. The access check compares the incoming message's `chatId` directly against the list. Empty list = all blocked (secure by default).

Same pattern as Telegram's `AllowedUserIds`.

## WhatsApp Markdown Conversion

| Markdown | WhatsApp |
|----------|----------|
| `**bold**` | `*bold*` |
| `*italic*` | `_italic_` |
| `~~strike~~` | `~strike~` |
| `` `code` `` | `` `code` `` |
| ` ```block``` ` | ` ```block``` ` |
| `[text](url)` | `text (url)` |
| Headings | `*heading*` (bold) |

Chunk at 4096 chars on paragraph/newline boundaries. If a chunk split falls inside an open formatting span (e.g., mid-bold or mid-code-block), close the span at the split point and re-open it at the start of the next chunk.

## Response Mode

Composing indicator + complete response. Show "composing..." presence while the LLM works, send the finished message when done. No streaming/draft updates (WhatsApp has no edit API).

## Configuration

`WhatsAppOptions` (deserialized from `Connection.Config`):

```csharp
public class WhatsAppOptions
{
    public List<string> AllowedChatIds { get; set; } = [];
}
```

Minimal for v1. Auth dir derived from dataPath convention, not configurable.

## DI and Dependencies

`WhatsAppChannelProviderFactory` injects `AgentEnvironment` (for `DataPath`) in addition to the same dependencies as `TelegramChannelProviderFactory` (`IConnectionStore`, `ILlmTextProvider`, provider key, model, `ILoggerFactory`). This is a new dependency not present in the Telegram factory — needed to construct the auth directory path.

`WhatsAppMessageHandler` receives `ILlmTextProvider` via the factory (same pattern as Telegram) and calls `CompleteAsync` on the derived conversation.

## QR Code Endpoint

- **Route**: `GET /api/connections/{connectionId}/whatsapp/qr`
- **Auth**: Standard API key auth (same as all other endpoints).
- **Response**: JSON `{"status":"unpaired|pairing|connected","qr":"base64-string-or-null"}`.
- **Behavior**: If the connection is unpaired, the endpoint starts the Node process and awaits the first QR event using a `TaskCompletionSource` with a 30s timeout (async, does not block a thread). If it times out, returns `{"status":"pairing","qr":null}` — client retries. If already pairing, returns the latest QR immediately. If connected, returns `{"status":"connected","qr":null}`.
- Frontend renders the QR string as an image (e.g., via a QR code library). The QR data is a string, not an image — rendering is the client's job.

## Logging

- Node process **stdout** is reserved for the JSON line protocol.
- Node process **stderr** is captured by .NET and forwarded to `ILogger<WhatsAppNodeProcess>` at appropriate log levels.
- The Node script uses `console.error()` for logging (writes to stderr), never `console.log()` (which would corrupt the protocol on stdout).

## Docker Changes

- Add `nodejs` + `npm` to the runtime stage of the Dockerfile.
- Copy `node/` directory into the image.
- Run `npm ci --omit=dev` during build (`--production` is deprecated in npm 9+).
- Baileys version pinned exactly in package.json (no `^` or `~` range). Updates require manual testing — Baileys is an unofficial library with frequent breaking changes.
- Node script path is a known convention relative to app root.

## Scope

1. New project: `OpenAgent.Channel.WhatsApp` with Node bridge
2. Conversation mapping fix: both Telegram and WhatsApp derive per-chat conversation IDs
3. QR endpoint: triggers pairing on demand
4. Dockerfile update: include Node.js runtime

## Known v1 Trade-offs

- **Dedup cache lost on restart**: The in-memory LRU is cleared when the app restarts, which is exactly when Baileys replays history. This can cause duplicate responses after a crash. Acceptable for v1; a persistent dedup set (SQLite table or file) can be added if this becomes a problem.
- **No per-chat rate limiting**: A user spamming messages triggers an LLM call per message. Not critical with a small AllowedChatIds list, but worth adding before opening access more broadly.
- **QR code expiry**: WhatsApp QR codes expire after ~20s. Baileys emits fresh QR events automatically. The client should poll the QR endpoint every ~15s to stay ahead of expiry. Not enforced server-side — documented for frontend implementors.

## Out of Scope (Future)

- Media messages (images, audio, video, documents)
- Group-specific settings (require mention, per-group tools)
- DM/group policy split (pairing, open, disabled)
- WebSocket QR endpoint for live pairing UI
- Streaming/chunked responses
