# Conversation Compaction

**Date:** 2026-03-15
**Issue:** https://github.com/mbundgaard/OpenAgent/issues/2

## Summary

When conversation context grows too large, compress old messages into a summary stored on the conversation. Extract durable facts to daily memory.

## Design

### Trigger

After each LLM response, check `prompt_tokens` from the API usage data. If it exceeds the configured threshold, spawn a compaction thread.

- **Trigger threshold:** configurable, e.g. 70% of max context window
- **Max context window:** configurable per deployment (e.g. 400k tokens)
- **Keep latest N message pairs:** configurable — these are never compacted

### Compaction Thread

1. Checks compaction lock — skip if already running
2. Sets compaction lock on the conversation
3. Reads all messages, splits into two sets:
   - **To compact:** all messages except the latest N pairs
   - **To keep:** the latest N pairs (never touched)
4. Reads `Conversation.Context` (previous summary, if any)
5. Sends the context + messages-to-compact to LLM with a system prompt instructing it to:
   - Output a new conversation context (summary)
   - Output a list of memories to write to daily memory
6. On success:
   - Updates `Conversation.Context` with the new summary
   - Deletes the compacted messages
   - Writes extracted memories to daily memory
   - Clears compaction lock
7. On failure:
   - Clears compaction lock
   - Retries on next response

### Context Building

`BuildChatMessages` produces: system prompt → `Conversation.Context` (if not null) → remaining messages → tool definitions.

Each compaction cycle rolls the previous context into the new summary. It's cumulative.

## Data Model Changes

### Conversation

- Add `Context` (string?, TEXT) — the compacted summary
- Add `CompactionRunning` (bool, INTEGER) — prevents concurrent compaction

### CompletionEvent

- Add `UsageInfo(int PromptTokens, int CompletionTokens)` — yielded after the final response so callers can access token usage

## Configuration

Compaction settings (configurable per deployment or globally):

- `MaxContextTokens` — model's context window size (e.g. 400000)
- `CompactionTriggerPercent` — trigger at this % of max (e.g. 70)
- `KeepLatestMessagePairs` — never compact these (e.g. 5)

## Implementation Steps

- [ ] 1. Capture `prompt_tokens` and `completion_tokens` from LLM response, yield as `UsageInfo` event
- [ ] 2. Add `Context` and `CompactionRunning` to Conversation model + SQLite schema + migration
- [ ] 3. Include `Conversation.Context` in `BuildChatMessages` (after system prompt, before messages)
- [ ] 4. Add compaction configuration model
- [ ] 5. Implement compaction logic (threshold check, lock, LLM call, update, delete)
- [ ] 6. Wire compaction trigger after LLM response in the provider
- [ ] 7. Design the compaction system prompt
- [ ] 8. Implement daily memory writes from extracted memories
