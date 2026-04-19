# Context Pruning — Drop Old Tool Rounds

GitHub: [#55](https://github.com/mbundgaard/OpenAgent/issues/55)

## Problem

Conversations grow monotonically because every assistant turn replays the full message history. Two columns in `Messages` contribute disproportionately to that growth:

1. **`Content` on tool-role rows** — today, `ToolResultSummary.Create` replaces the full result with a `{tool, status, size}` stub **at persist time**. The agent sees the full result only within the turn that produced it, and never again. This is too aggressive: a code-review session reading five files can reference them within the current turn but has to re-read on any follow-up.
2. **`ToolCalls` on assistant rows** — the serialized `function.arguments` payload is persisted verbatim and never pruned. In the real 416-turn DM conversation, this column holds **201K chars (~50K tokens)**, larger than all user+assistant text combined. It includes entire `file_write` bodies that are already on disk and `shell_exec` curl commands (some containing secrets).

Measured on the real DM conversation:

| What's persisted today | chars | ~tokens |
|---|--:|--:|
| Assistant text | 153K | 38K |
| Assistant `ToolCalls` JSON | 201K | 50K |
| User text | 53K | 13K |
| Tool stubs | 14K | 3K |
| **Persisted total** | **~420K** | **~105K** |
| Full tool results, had we kept them | +682K | +170K |

**Goal:** keep recent tool rounds in the prompt so the agent can reason across them, and drop older rounds entirely on a schedule — no stubs, no tombstones. The assistant's own text messages ("I found X in config.json") preserve the narrative; the raw tool I/O is redundant once a round is old enough.

## Goals

- Agent can reference recent tool results and tool-call arguments across turns within a bounded window.
- Older tool rounds are removed from the prompt entirely. No tombstones, no stubs. The assistant's prose carries the narrative.
- Background purge runs as a system job; no coupling to the LLM request path.
- Purging is additive — no rewrite of existing summarized rows, no data migration.

## Non-goals (v1)

- Per-tool policy. Uniform age cutoff and uniform keep-last-K across all tools.
- "Keep forever" markers. If something matters beyond the window, write it to a file.
- Redaction of secrets at write time (separate concern — this design shortens exposure, doesn't eliminate it).

## Design

### Write path — persist raw

Providers stop calling `ToolResultSummary.Create` at persist time. The full tool result is written to `Content`. A new column `ToolType` records the tool name. The assistant message's `ToolCalls` JSON is persisted verbatim as today.

```csharp
agentLogic.AddMessage(conversationId, new Message
{
    Id = Guid.NewGuid().ToString(),
    ConversationId = conversationId,
    Role = "tool",
    Content = result,              // full, raw
    ToolType = name,               // NEW
    ToolCallId = id,
    Modality = MessageModality.Text
});
```

`ToolResultSummary` is deleted — with the drop-the-round approach, no code produces stubs.

### Schema changes

Three nullable columns on `Messages`, added via existing `TryAddColumn` migration pattern:

```sql
ALTER TABLE Messages ADD COLUMN ToolType TEXT;
ALTER TABLE Messages ADD COLUMN ToolResultPurgedAt TEXT;
ALTER TABLE Messages ADD COLUMN ToolCallsPurgedAt TEXT;
```

- `ToolType`: populated on every new tool-role row going forward. NULL on existing rows — the purge job skips them (they're already-summarized stubs from the old behavior). Not strictly required for the purge predicate (we could use `Role = 'tool' AND ToolCallId IS NOT NULL`), but earns its keep for diagnostics ("which tools produce the most pruned content?") and for future per-tool policies without re-parsing `Content` JSON.
- `ToolResultPurgedAt`: timestamp set when the row's `Content` was nulled. NULL = live.
- `ToolCallsPurgedAt`: timestamp set when the row's `ToolCalls` was nulled. NULL = live.

Timestamps instead of booleans so we can answer "when was this purged?" in diagnostics. Stored as `DateTimeOffset.ToString("O")` — the same format as `Messages.CreatedAt`, so all timestamp comparisons are lexicographic-safe.

### Purge job

Implemented as an `IHostedService` — one more system job alongside the memory indexer. When the `ISystemJob` abstraction lands ([CLAUDE.md memory notes](../../CLAUDE.md)), this becomes one of its drivers.

**Triggers (either fires a purge run for the affected conversations):**

1. **Scheduled** — hourly catch-up. Runs across all conversations. Cadence chosen from the real data: peak 1-hour burst on the DM is 40 tool-calls (~34K tokens of pile-up), comfortably below the 149K headroom to the 70% compaction trigger. 15-min cadence was considered and rejected as unnecessary.
2. **Reactive** — when a conversation's `LastPromptTokens` crosses `ReactivePurgeThresholdPercent` (default 60% of `CompactionConfig.MaxContextTokens`), trigger the purge for that conversation at the end of the turn. The 60%/70% ordering is deliberate: purge fires strictly before compaction would, and in most conversations will prevent compaction from ever needing to run. This is one of the design's wins — cheaper, more precise pruning instead of a whole-history LLM summarization pass.

**Policy.** The unit of evaluation is the **round** — one assistant row with `ToolCalls IS NOT NULL` plus all `tool`-role rows referencing its call ids. A round is purged only if **all** of:
- The assistant row has `ToolCalls IS NOT NULL AND ToolCallsPurgedAt IS NULL`.
- The round is outside the last K such assistant rows in its conversation (default K=10), ranked newest-first by `rowid`.
- The assistant row's `CreatedAt` is older than the age cutoff (default 24h).

A round survives if **either** recency condition fails. The last K rounds stay even if older than the cutoff; any round inside the cutoff stays even if outside K. Everything else has both its assistant `ToolCalls` and its result children nulled together.

**Execution shape — purge by round, not by row.** Parallel tool calls (one assistant row with N tool_result children) would misalign if the two columns were ranked independently: 10 assistant rows ≠ 10 tool-result rows when N > 1, so the boundary could leave an assistant's `ToolCalls` live while its children are nulled, or vice versa — producing API-400-worthy orphans.

Instead, the unit of purge is the **round** (one assistant tool_calls row + all its matching tool_result rows). Ranking is done on assistant rows only; a round is purged atomically, cascading to its children via `ToolCallId`.

Per conversation, in a single transaction:

1. **Select candidate rounds** (assistant rows with `ToolCalls IS NOT NULL AND ToolCallsPurgedAt IS NULL`, ranked newest-first by `rowid`, outside the last K, older than the cutoff).
2. **Parse each candidate's `ToolCalls` JSON** in C# to extract the call ids it references. Accumulate into a flat list.
3. **UPDATE 1** — null `ToolCalls` + set `ToolCallsPurgedAt` on the candidate assistant rowids.
4. **UPDATE 2** — null `Content` + set `ToolResultPurgedAt` on all `Messages` rows where `ConversationId = @cid AND ToolCallId IN (@extractedIds)`.
5. Commit transaction.

Wrapping both UPDATEs in one transaction per conversation eliminates the "A succeeds, B fails" window. Errors are caught and logged per conversation; a failure on one conversation does not block the others.

```
for each conversation in Conversations:
    try:
        using tx = db.BeginTransaction()
        candidates = selectCandidateAssistantRounds(conversation.Id, keepLast, cutoff)
        toolCallIds = candidates.SelectMany(row => parseToolCallIds(row.ToolCalls))
        purgedAt = DateTimeOffset.UtcNow.ToString("O")
        updateAssistantRows(candidates.rowids, purgedAt)
        updateToolResultRows(conversation.Id, toolCallIds, purgedAt)
        tx.Commit()
        log (rounds, resultRows) affected
    catch e:
        log error; continue next conversation
```

**SQL — selecting candidate rounds:**

```sql
WITH ranked AS (
  SELECT rowid, ToolCalls
  FROM Messages
  WHERE ConversationId = @cid
    AND ToolCalls IS NOT NULL
    AND ToolCallsPurgedAt IS NULL
  ORDER BY rowid DESC
)
SELECT rowid, ToolCalls
FROM ranked
WHERE rowid NOT IN (
        SELECT rowid FROM ranked LIMIT @keepLast
      )
  AND CreatedAt < @cutoff;
```

**UPDATE 1 — assistant rows:**

```sql
UPDATE Messages
SET ToolCalls = NULL,
    ToolCallsPurgedAt = @purgedAt
WHERE rowid IN (@candidateRowids);
```

**UPDATE 2 — their tool-result children:**

```sql
UPDATE Messages
SET Content = NULL,
    ToolResultPurgedAt = @purgedAt
WHERE ConversationId = @cid
  AND ToolCallId IN (@extractedIds)
  AND ToolResultPurgedAt IS NULL;
```

`@purgedAt` is computed in C# as `DateTimeOffset.UtcNow.ToString("O")` — same format as `CreatedAt`, keeping all timestamp comparisons consistent. Cutoff is computed as `DateTimeOffset.UtcNow - PurgeAgeCutoff` in the same format.

Scoping to a single conversation keeps the queries trivial and makes per-conversation logging natural: `log.Info("purged {Rounds} rounds, {ResultRows} result rows from {ConversationId}")`.

### Read path — skip purged rounds

Both text providers' message builders (`AzureOpenAiTextProvider.BuildChatMessages` for OpenAI Chat Completions and `AnthropicSubscriptionTextProvider`'s equivalent for Anthropic content blocks) get the same two rules, expressed in each provider's protocol:

1. **Assistant row with `ToolCallsPurgedAt != NULL`:**
   - If the row has text `Content`: render as a plain assistant text message — drop the `tool_calls` array (OpenAI) or drop the `tool_use` content blocks (Anthropic).
   - If the row has no text content: skip it entirely.

2. **Tool-role row with `ToolResultPurgedAt != NULL`:** skip entirely.

Because rounds are purged atomically (assistant + children together), the two fields are always consistent — an assistant row whose `ToolCalls` is NULL never has live children, and vice versa. The existing orphan-skip code in `BuildChatMessages` remains in place as a belt-and-braces defense against any mid-run inconsistencies or future bugs.

### Voice sessions and write path

The `ToolResultSummary.Create` persist-site appears in five places that all need updating to persist raw:

- `AzureOpenAiTextProvider`
- `AnthropicSubscriptionTextProvider`
- `AzureOpenAiVoiceSession`
- `GeminiLiveVoiceSession`
- `GrokVoiceSession`

Voice sessions don't replay the stored transcript the way text providers do — they operate on realtime session state. The persist-site change aligns the DB with the new model (tool-role rows carry full content + `ToolType`); the purge job applies uniformly to conversations regardless of modality. Voice-session read paths need no changes.

`ToolResultSummary` is deleted after the five call sites are updated.

### Skill deactivation hook

`DeactivateSkillTool.ExecuteAsync` runs one extra statement at the end: null `Content` and set `ToolResultPurgedAt = now` for all unpurged `activate_skill_resource` results in the conversation. Scope: all of them, not just the deactivated skill's — trivially correct, and loses at most a few KB of recency on other active skills' resources. The user can always re-call the tool.

The matching assistant rows (whose `ToolCalls` reference the resource loader) get cleaned up by the normal scheduled run when they age out.

## Configuration

Add to `AgentConfig`:

```csharp
public int PurgeKeepLast { get; init; } = 10;
public TimeSpan PurgeAgeCutoff { get; init; } = TimeSpan.FromHours(24);
public TimeSpan PurgeScheduledInterval { get; init; } = TimeSpan.FromHours(1);
public int PurgeReactiveThresholdPercent { get; init; } = 60;
```

All four have sane defaults; none require tuning to ship.

## Compatibility

- Existing rows have `ToolType = NULL` → skipped by the purge job. Their already-summarized `Content` (from the current `ToolResultSummary` behavior) stays as-is, rendered as today. No backfill.
- API wire format: OpenAI Chat Completions and Anthropic Messages both accept the absence of tool_calls/tool_results. Rounds dropped together don't orphan IDs. No provider changes beyond the persistence call site across five providers (see above) and the read-path skip rules in the two text providers.
- Frontend: the conversations API returns raw `Content` and `ToolCalls`, which will now be NULL on purged rows. Conversation viewers should render purged tool-role rows as a grey placeholder (`[purged]` or similar) and hide the `tool_calls` section on purged assistant rows. One-liner UI change, out of scope for the backend plan but should be tracked as a follow-up issue.

## Testing

- **Unit** on the purge service: given `Messages` rows across N conversations with various ages, the purge keeps the last K rounds per conversation regardless of age, and otherwise nulls rounds older than the cutoff. `ToolType IS NULL` rows are untouched.
- **Unit — parallel tool calls**: a single assistant row with three tool_calls produces three tool-result rows; when that round is purged, all three children are nulled in the same transaction; when the round is kept, none are nulled.
- **Unit** on `BuildChatMessages` skip rules (both OpenAI and Anthropic builders): purged assistant row with text renders as plain assistant; without text, skipped. Purged tool result is skipped.
- **Integration** end-to-end: `CompleteAsync` persists a full `file_read` result; a purge run nulls it together with its assistant tool_calls row; the next turn rebuilds the prompt with that round omitted; Azure and Anthropic providers both accept the request.
- **Integration — atomicity**: force UPDATE 2 to fail mid-transaction (e.g. by corrupting a parameter); verify UPDATE 1 is rolled back and the row remains live for the next run.
- **Skill resource cleanup**: activate a skill, load a resource, deactivate the skill, verify the resource row is nulled immediately (not waiting for the scheduled run).
- **Reactive trigger**: force `LastPromptTokens > threshold` on a test conversation and verify the purge fires end-of-turn for only that conversation.

## Out of scope

- Per-tool-type windows. Future evolution: rank assistant rows within partitions keyed by the first tool name in `ToolCalls`. Not a one-line change given the round-based model, but achievable.
- Secret redaction at write time (`shell_exec` args with tokens). Purging after a window shortens exposure but does not eliminate it.
- Compaction integration. With default settings (purge `PurgeAgeCutoff = 24h` and `PurgeKeepLast = 10` vs compaction's `KeepLatestMessagePairs = 5`), any tool round old enough to be purged is also far outside compaction's live window — if compaction fires, it summarizes prose around already-nulled tool content, which is exactly the intended outcome. No coordination needed.

## Open questions

- Default `PurgeKeepLast = 10` and `PurgeAgeCutoff = 24h` are guesses. Revisit once the feature lands — could be 5 or 15 with equal justification.
