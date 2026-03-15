# Conversation Compaction

**Date:** 2026-03-15
**Issue:** https://github.com/mbundgaard/OpenAgent/issues/2

## Summary

When conversation context grows too large, compress old messages into a structured summary with message references, stored on the conversation. Extract durable facts to daily memory. Messages are preserved (never deleted) but excluded from the active context — the summary becomes the agent's view of the past, with the ability to retrieve specific original messages on demand.

## Architecture

All compaction logic lives in **`OpenAgent.ConversationStore.Sqlite`** — no new projects or interfaces. The store already owns persistence; compaction is another persistence concern.

- **`GetMessages`** becomes the smart method: excludes compacted messages, prepends the compaction summary as the first message
- **`Update`** checks `LastPromptTokens` against the compaction threshold and triggers compaction internally when needed
- **The provider changes nothing** — it already calls `Update(conversation)` after setting `LastPromptTokens`. The store reacts to that.

The provider's `BuildChatMessages` assembles: system prompt (from `IAgentLogic`) + messages (from `IConversationStore.GetMessages`, which now includes the summary and excludes compacted messages) + tool definitions (from `IAgentLogic`).

## Design

### Trigger

The provider already updates `Conversation.LastPromptTokens` and calls `IConversationStore.Update()` after each LLM response. The store checks if `LastPromptTokens` exceeds the configured threshold and spawns compaction internally.

- **Trigger threshold:** configurable, e.g. 70% of max context window
- **Max context window:** configurable per deployment (e.g. 400k tokens)
- **Keep latest N message pairs:** configurable — these are never compacted

### Compaction Thread

1. Checks compaction lock — skip if already running
2. Sets compaction lock on the conversation
3. Reads messages after `CompactedUpToRowId` (the live messages), splits into two sets:
   - **To compact:** all live messages except the latest N pairs
   - **To keep:** the latest N pairs (never touched)
4. Reads `Conversation.Context` (previous summary, if any)
5. Sends the context + messages-to-compact to LLM with a compaction system prompt instructing it to:
   - Output a new conversation context (structured summary with message references)
   - Output a list of memories to write to daily memory
6. On success:
   - Updates `Conversation.Context` with the new summary
   - Updates `Conversation.CompactedUpToRowId` to the last compacted message's ID
   - Writes extracted memories to daily memory
   - Clears compaction lock
7. On failure:
   - Clears compaction lock
   - Retries on next `Update` call

### Context Structure

The context is a structured summary organized by topic, with timestamps and message references:

```
## Auth Design (2026-03-15 09:12 – 09:45)
Decided on JWT with refresh tokens. Access token expiry 24h, refresh 7d.
User rejected OAuth2 in favor of simple API key auth for v1.
[ref: msg_12, msg_14, msg_15, msg_18, msg_22]

## File Tool Debugging (2026-03-15 09:50 – 10:15)
Fixed UTF-8 BOM issue in file write tool. Root cause: StreamWriter default encoding.
Agent read src/Tools/FileSystemToolHandler.cs, ran tests, confirmed fix.
[ref: msg_30, msg_34, msg_38, msg_41]
```

Each compaction cycle rolls the previous context into the new summary. Old references carry forward — IDs stay stable because messages are never deleted.

### Tool Call Handling

Tool calls and results are treated differently during compaction:

- **Tool requests** (what the agent tried to do) — summarized and referenced by message ID
- **Tool results** (raw data returned) — **not referenced**. The outcome is captured in the summary text. If the agent needs the raw data again, it can re-execute the tool (re-read the file, re-run the command)

This keeps references meaningful — they point to decision-making messages, not bulk data.

### Message Retrieval

The agent gets an `expand` tool that takes message IDs and returns the original messages verbatim. This is a simple direct lookup, not a search — the agent already knows which IDs to expand because they're in the context summary.

### Context Building

`GetMessages` returns: `Conversation.Context` as a system message (if not null) → messages after `CompactedUpToRowId` in order.

The provider's `BuildChatMessages` then prepends the system prompt and appends tool definitions as before.

## Data Model Changes

### Conversation

- Add `Context` (string?, TEXT) — the structured compaction summary
- Add `CompactedUpToRowId` (long?, INTEGER) — SQLite rowid of the last message included in the summary. Messages with rowid > this value are live; messages up to and including it are compacted. Null means no compaction has occurred.
- Add `CompactionRunning` (bool, INTEGER) — prevents concurrent compaction

### Message

- Add `RowId` (long) property to the model — not a schema change, SQLite already has it. Must be explicitly selected: `SELECT rowid, * FROM Messages`. Needed so the store can track compaction boundaries and pass rowid through to the compaction logic.

## Configuration

Compaction settings (configurable per deployment or globally):

- `MaxContextTokens` — model's context window size (e.g. 400000)
- `CompactionTriggerPercent` — trigger at this % of max (e.g. 70)
- `KeepLatestMessagePairs` — never compact these (e.g. 5)

## Implementation Steps

- [ ] 1. Add `RowId` property to Message model. Update all SQLite queries that read messages to `SELECT rowid, *` and populate it.
- [ ] 2. Add `Context`, `CompactedUpToRowId`, and `CompactionRunning` to Conversation model + SQLite schema migration
- [ ] 3. Update `GetMessages` in SQLite store to return messages after `CompactedUpToRowId`, prepending `Conversation.Context` as a message
- [ ] 4. Add compaction configuration model
- [ ] 5. Implement compaction logic inside the SQLite store (threshold check on `Update`, lock, LLM call, update context + cutoff rowid)
- [ ] 6. Design the compaction system prompt (topic grouping, timestamps, message references, tool call handling)
- [ ] 7. Implement `expand` tool for retrieving original messages by ID
- [ ] 8. Implement daily memory writes from extracted memories
