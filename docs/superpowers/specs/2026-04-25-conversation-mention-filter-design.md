# Per-Conversation Mention Filter — Design

**Date:** 2026-04-25
**Status:** Approved, pending implementation plan

## Problem

The agent currently replies to every inbound user message that passes connection-level access control. In group chats (Telegram supergroups, WhatsApp groups), this means the bot responds to every single message — fine for a dedicated DM, disruptive for a shared group where the bot should only chime in when addressed.

We need a per-conversation control that filters inbound user messages so the agent only engages when its name (or another trigger word) is present.

## Non-Goals

- **Reply-to-bot / native quote detection.** Telegram and WhatsApp both expose reply metadata; using it to infer a mention is a natural extension, but deferred.
- **Word-boundary matching.** Case-insensitive substring is the only matching rule in v1.
- **UI editor.** The list is editable via API only. A conversation-settings UI can follow later.
- **Auto-population.** When a group conversation is first created, we do not auto-seed the list with the bot's username. The operator sets it explicitly.

## Design

### Model

Add a nullable string list to `Conversation`:

```csharp
[JsonPropertyName("mention_names")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public List<string>? MentionNames { get; set; }
```

Semantics:

- `null` or empty list → reply to all (current behavior, default for new and existing conversations).
- Non-empty list → drop inbound user messages whose text does not contain any of the configured names.

This mirrors the existing `ActiveSkills` shape — same nullability, same JSON treatment, same storage pattern.

### Storage

New column `mention_names TEXT NULL` on the `conversations` table. Added via the existing `TryAddColumn` migration helper in `SqliteConversationStore`. Serialization/hydration exactly mirrors `ActiveSkills` (JSON array text).

Load path: JSON-decode to `List<string>?`; treat empty/whitespace strings as absent so an accidental `[""]` doesn't match everything.

Save path: serialize via `Update(conversation)`. `null` and `[]` both persist as `NULL` in the column — no semantic difference between them.

### Filtering

Single helper in `OpenAgent.Models.Conversations`:

```csharp
public static class MentionFilter
{
    public static bool ShouldAccept(Conversation conversation, string userText)
    {
        if (conversation.MentionNames is null or { Count: 0 })
            return true;

        return conversation.MentionNames.Any(name =>
            !string.IsNullOrEmpty(name) &&
            userText.Contains(name, StringComparison.OrdinalIgnoreCase));
    }
}
```

Called at every inbound-user-text entry point, **before** any side effect (conversation persistence, typing/composing indicator, LLM call):

| Entry point | Location | Placement |
|---|---|---|
| Telegram | `TelegramMessageHandler.HandleUpdateAsync` | After the text-only type filter, before `SendTypingAsync` |
| WhatsApp | `WhatsAppMessageHandler.HandleMessageAsync` | After the dedup check, before `SendComposingAsync` |
| REST chat | `ChatEndpoints` POST `/api/conversations/{id}/messages` | Before provider resolution |
| Webhook push | `WebhookEndpoints` POST `/api/webhook/conversation/{id}` | Before enqueueing the completion trigger |

On drop: no database write, no assistant turn started, no typing/composing indicator. One `LogDebug` line per drop (`"Mention filter dropped message in conversation {ConversationId}"`) for traceability without log spam.

The filter runs after conversation lookup — we need the `Conversation` row to read `MentionNames` — but before we mutate any state.

### Channel-specific note: Telegram `NewChatMembers`

The bot-added-to-group code path (`HandleBotAddedToGroupAsync`) runs before the text-message filter and creates the conversation so the group is known. It doesn't route text to the LLM, so no filter is needed there. First text messages in that new group go through the main flow and hit the filter normally.

### API

Extend `UpdateConversationRequest` with an optional `MentionNames : List<string>?`. In the existing `PATCH /api/conversations/{conversationId}` handler:

- Field omitted or null in the request → leave unchanged (matches the pattern used by `Provider`, `Model`, `Source`, etc.).
- Empty list `[]` → clear (back to reply-all).
- Non-empty list → replace wholesale.

Surface the field in both `ConversationListItemResponse` and `ConversationDetailResponse` so the UI can show whether a conversation is filtered.

### Testing

Integration tests using `WebApplicationFactory`:

1. **PATCH round-trip.** PATCH with `["Dex"]`, GET reflects the list. PATCH with `[]` clears it; a subsequent GET omits the field (null is not serialized, per `JsonIgnoreWhenWritingNull`).
2. **Drop behavior via REST chat.**
   - With `MentionNames = ["Dex"]`, `POST /api/conversations/{id}/messages` with body "hello" returns a response, but the message store for that conversation is unchanged and no assistant message is persisted.
   - With the same config, POST "hey Dex what's up" produces a normal assistant turn.
3. **Case-insensitive match.** With `["dex"]`, "DEX!" passes.
4. **Substring behavior (documented).** With `["Dex"]`, "index" passes — this is the chosen v1 semantics and the test pins it so future word-boundary changes are deliberate.
5. **Null mention list.** With `MentionNames = null` (default), any message passes.

Channel handlers already have integration coverage; a single unit test over `MentionFilter.ShouldAccept` covers their branch without spinning up platform mocks.

## Rollout

- Back-compat: existing conversations load with `MentionNames = null`. No behavior change unless explicitly configured.
- Migration: additive column via `TryAddColumn`. Existing DBs are upgraded in place on startup.
- No config-file changes, no new environment variables.
