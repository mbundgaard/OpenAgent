# Context Management Rewrite

**Date:** 2026-04-24
**Status:** Design — decisions locked. PR 1 plan: [`2026-04-24-context-management-rewrite-plan.md`](2026-04-24-context-management-rewrite-plan.md).

## Summary

Separate conversation persistence from LLM context construction. Today, `ToolResultSummary.Create` destroys tool output on persist, compaction cuts by message count, and the compaction summary is injected as a `system` message. These three choices compound — persisted history isn't useful for compaction, cuts can split tool-call/tool-result pairs (silently dropped by the orphan check in `BuildChatMessages`), and a changing system message breaks prompt caching.

Rewrite: keep full tool results on disk referenced by a new SQLite column; compute the LLM view at request build time; choose cut points at turn boundaries; move the summary to a `user` message wrapped in `<summary>` tags; add overflow-triggered and manual compaction; drive thresholds from the active model's context window; make iterative summaries a first-class behavior.

## Goals

- Tool results remain retrievable across turns without bloating SQLite.
- Compaction never splits a `tool_calls` → `tool` pair.
- The real system prompt stays stable across turns (cache-friendly).
- Compaction can be triggered proactively (threshold), reactively (overflow), and manually.
- Summaries accumulate across repeated compactions rather than being rewritten from scratch.
- No change to `IAgentLogic`'s "injected context, not orchestrator" principle. All changes land in providers, the summarizer, and the SQLite store.
- Compaction remains silent from the user's perspective — a background operation, not a UX event.

## Non-Goals

- Multi-branch conversations (pi's `/tree` / `/fork`). Out of scope — channels don't need it, and the cost outweighs the benefit here.
- Replacing `IConversationStore` or splitting the `Messages` table into an entry-type union. Current schema is sufficient with two additive changes.
- MemoryIndex integration of compaction output. The memory pipeline (daily files → indexer → `search_memory` / `load_memory_chunks`) stays completely separate from compaction.

## Current Behavior (baseline)

What happens today, grounded in the code:

- **Within a turn** (`AzureOpenAiTextProvider.CompleteAsync`, `AnthropicSubscriptionTextProvider.CompleteAsync`): `BuildChatMessages` reads the full stored history, and the tool loop mutates the live `request.Messages` list with **full** tool results as it executes them. The current turn sees everything.
- **Persistence:** assistant-with-tool-calls messages are written verbatim; `tool` role messages are persisted with `Content = ToolResultSummary.Create(name, result)` → `{"tool":"name","status":"ok","size":N}` or an error variant.
- **Between turns:** `BuildChatMessages` rebuilds from the store, so the LLM sees only the summary stubs — full tool output is unrecoverable.
- **Compaction:** `SqliteConversationStore.TryStartCompaction` fires from `Update` when `LastPromptTokens >= TriggerThreshold` (defaults: 400k window, 70%, keep last 5 pairs). `RunCompactionAsync` drops the last 10 messages, summarizes everything older via `CompactionSummarizer.SummarizeAsync`, stores the result as `Conversation.Context` (a string) and records `CompactedUpToRowId`. `GetMessages` prepends `Context` as a `system` message and returns messages with `rowid > CompactedUpToRowId`.
- **Safety net:** `BuildChatMessages` skips orphaned tool-call rounds to avoid 400s. This masks — but doesn't prevent — bad cut points.

## Problems

| # | Issue | Consequence |
|---|---|---|
| 1 | `ToolResultSummary` replaces tool output on persist. | Next turn has no access to prior tool output. Compaction summarizer sees only stubs, so summaries lose fidelity. Debugging a past conversation is impossible. Memory-retrieval tools (`search_memory`, `load_memory_chunks`) silently lose their results after the turn, forcing the agent to re-search for things it already knows it knows. |
| 2 | Cut point is count-based (`liveMessages.Count - keepCount`). | Can land inside an assistant's tool-call round. The orphan check skips that round silently, so the LLM loses context without warning. |
| 3 | Compaction summary is a `system` message that changes each compaction. | Breaks prompt caching of the real system prompt (Anthropic is strict here; Azure also caches system prefix). |
| 4 | Only one trigger (threshold, quiet). | No recovery from context-overflow errors mid-turn. No way for the user or a scheduled task to force compaction. |
| 5 | `MaxContextTokens = 400_000` is hardcoded. | Wrong for smaller models. No concept of per-model window. |
| 6 | Single-pass summary, no iterative update semantics. | Each new compaction can drop detail from the previous one — no explicit "carry forward Done/Decisions, update In Progress/Next Steps". |
| 7 | Fire-and-forget compaction `Task.Run` with no `CancellationToken`. | Races with concurrent writes in multi-channel conversations; no abort on conversation delete; no retry path. |
| 8 | `CompactionResult.Memories` is extracted but not consumed. | Wasted tokens in the compaction prompt for a field that goes nowhere. |

## Design

### Principle

> **Store everything. Compute the LLM view.**

`IConversationStore.GetMessages(conversationId)` stays as a persistence accessor — returns raw stored history with references. A new optional parameter lets callers opt in to loading full tool result blobs from disk when they need them (providers do, the UI doesn't).

The provider then builds the LLM-facing `ChatMessage[]` / Anthropic `messages[]` list by:

1. Calling `GetMessages(conversationId, includeToolResultBlobs: true)` — tool results come back with full content attached via a transient `FullToolResult` field.
2. Determining the effective cut point (existing `CompactedUpToRowId` or none).
3. If compacted: emitting `Conversation.Context` as a **user** message (`<summary>...</summary>`) in place of the pre-cut history.
4. Emitting kept messages verbatim, using `FullToolResult` for tool messages when present, falling back to `Content` (the compact summary) when the blob is missing or not loaded.

The `IAgentLogic` / `IConversationStore` contract stays almost the same — one optional parameter added to `GetMessages`. The shift is inside the providers and the store's internal helpers.

### UI vs LLM separation

Explicit split between the two consumers of conversation history:

| Consumer | Method | What they see |
|---|---|---|
| **UI** — React app, REST `GET /api/conversations/{id}` | `GetMessages(id)` (default, `includeToolResultBlobs: false`) | Post-cut messages only. Compact summaries in `Content` for tool results. **No compaction summary message** — `Conversation.Context` is a separate field the UI can render as a sidebar panel if desired, but it doesn't appear in the timeline. |
| **LLM** — Provider's `BuildChatMessages` | `GetMessages(id, includeToolResultBlobs: true)` + `Conversation.Context` | Post-cut messages with full tool results inlined. Compaction summary injected as a user message wrapped in `<summary>` tags. |

The compaction summary **does not appear in the UI timeline** by design. End-users never see or feel compaction — it runs silently in the background. `Conversation.Context` is exposed on the API response for any future UI that wants to render it explicitly.

### Schema changes

Two additive changes, both via `TryAddColumn`:

- `Messages.ToolResultRef TEXT NULL` — relative path to the full tool result file on disk (e.g., `"tool-results/a1b2c3d4.txt"`). `NULL` for non-tool messages and for older rows written before this migration.
- `Message.FullToolResult` (C# transient, `[JsonIgnore]`) — **not persisted.** Set by providers on write (source for the blob); populated by the store on read when `includeToolResultBlobs: true`.

`Content` continues to carry the compact summary for every tool result, so every existing reader keeps working during and after migration.

**Migration strategy:** write both `ToolResultRef` and `Content` going forward. Old rows have `NULL` in `ToolResultRef` — no retroactive repair. Readers prefer `FullToolResult` (loaded via `ToolResultRef`) when present, fall back to `Content`.

### Blob storage layout

Per-conversation directory under the data path:

```
{dataPath}/conversations/{conversationId}/tool-results/{messageId}.txt
```

- **One file per tool result message.** Filename is the message's GUID — safe as a filename, unique per conversation.
- **Write ordering:** file first (to `{path}.tmp`, then `File.Move` for atomicity), then SQL `INSERT` with the new `ToolResultRef`. On crash you get orphaned files (recoverable via periodic sweep) rather than broken refs.
- **Read:** missing file on load → log warning, set `FullToolResult = null`, provider falls back to `Content`. Never throws — a missing blob must not break an LLM turn.
- **Delete:** `IConversationStore.Delete(conversationId)` removes `{dataPath}/conversations/{conversationId}/` recursively after deleting rows. Idempotent.
- **Encoding:** UTF-8 no BOM, matching the `FileSystemToolHandler` constant.
- **No separate interface.** Per DRY/YAGNI, blob helpers stay inside `SqliteConversationStore` as private methods (`SaveToolResultBlob`, `ReadToolResultBlob`, `DeleteConversationBlobs`). The class already receives `AgentEnvironment` for the data path.

### Cut point algorithm

Replace the count-based cut in `RunCompactionAsync` with a token-accumulating walk:

```
Input: live messages (after current CompactedUpToRowId), keepRecentTokens
1. accumulated := 0
2. walk messages from newest to oldest:
     accumulated += estimateTokens(msg)   // chars/4, with per-role specializations
     if accumulated >= keepRecentTokens:
       cutIndex := index of the nearest earlier message whose role is "user"
                   (fallback: "assistant" without tool_calls; never a "tool" result)
       break
3. if no cut found (conversation entirely inside keepRecentTokens): don't compact
4. return entries[0..cutIndex) as "to summarize", entries[cutIndex..] as "to keep"
```

Key rule: **the cut always lands at a `user` message, or at an `assistant` message whose `ToolCalls` is null.** Never at a `tool` result — that would split the pair that follows an assistant. Since tool results come after their assistant in `rowid` order, snapping to the nearest earlier non-tool boundary is sufficient.

`estimateTokens` is a chars/4 conservative estimate:
- `user` / `assistant` text: length of `Content`.
- `assistant` with `ToolCalls`: text + length of the serialized `ToolCalls` JSON.
- `tool` result: length of `FullToolResult` when loaded, else `Content`. Cap at some ceiling (say 50k chars) to avoid one huge result dominating the walk.

`keepRecentTokens` (new config, replaces `KeepLatestMessagePairs`) defaults to 20_000 — roughly one long turn plus immediate context.

### Summary as a user message

In provider `BuildChatMessages`, when `Conversation.Context` is present:

```csharp
chatMessages.Add(new ChatMessage
{
    Role = "user",
    Content =
        "The conversation history before this point was compacted into the following summary:\n\n" +
        "<summary>\n" + conversation.Context + "\n</summary>"
});
```

This replaces the current `role = "system"` synthetic message in `GetMessages`. The real system prompt (from `SystemPromptBuilder`) stays first and unchanged across turns.

### Iterative summary prompt

Split the compaction prompt into two:

- **`CompactionPrompt.Initial`** — current prompt (topic-grouped with `[ref: ...]`), minus the `memories` field.
- **`CompactionPrompt.Update`** — receives the previous summary in `<previous-summary>` tags; rules include "preserve all existing topics and refs, append new ones, update timestamps".

Both prompts include an explicit rule about memory-retrieval preservation:

> When the conversation includes `search_memory` or `load_memory_chunks` tool calls, preserve the *content* of what was retrieved in your summary — not just the fact that a lookup happened. The summary is the agent's working memory after the cut; if the content is missing, the agent will re-search for things it already found.

`CompactionSummarizer.SummarizeAsync` picks Initial when `existingContext` is null, Update otherwise. Response is a plain string now (`memories` field dropped from the JSON response shape).

### Triggers

Three entry points, all landing in a single `PerformCompactionAsync(conversationId, reason, customInstructions?, ct)`:

| Reason | Source | Retry? |
|---|---|---|
| `Threshold` | `SqliteConversationStore.Update` when `LastPromptTokens >= TriggerThreshold(activeModelWindow)`. Existing path, but threshold is now per-conversation model. Background task. | No |
| `Overflow` | Provider catches the LLM's context-length error, calls `PerformCompactionAsync(Overflow)`, then retries the same turn **once**. A second overflow surfaces as a user-visible error. Synchronous from the turn's viewpoint. | Yes, one retry |
| `Manual` | `POST /api/conversations/{id}/compact` with optional body `{"instructions": "..."}`. Synchronous. No self-compaction tool, no channel slash-command. | No |

All three call the same core. Silent from the user's perspective — no UI surface, no chat messages about it.

### Per-model context window

Add to `Conversation`:

```csharp
public int? ContextWindowTokens { get; set; }  // Cached from provider when set.
```

Populate it lazily from a new `ILlmTextProvider.GetContextWindow(model)` when missing. Compute threshold dynamically:

```csharp
var window = conversation.ContextWindowTokens ?? compactionConfig.MaxContextTokens;
var trigger = window * compactionConfig.CompactionTriggerPercent / 100;
if (conversation.LastPromptTokens >= trigger) ...
```

This keeps `CompactionConfig.MaxContextTokens` as a sane fallback for providers that don't expose the window, and scales automatically for future models.

### Concurrency

The `CompactionRunning` flag stays. Add:

- A `ConcurrentDictionary<string, CancellationTokenSource>` keyed by conversation ID, owned by the store. Cancelled on `Delete` and on host shutdown.
- A small critical section around the "read live messages → summarize → persist cutoff" sequence so a message inserted mid-compaction doesn't get swallowed by a stale cutoff. Simplest form: take the conversation's `LastRowId` at the start of summarization, only set `CompactedUpToRowId` to a rowid ≤ that value. New messages arriving during summarization stay uncompacted, to be picked up on the next run.

### Observability

Structured Info logs at each phase. No UI affordance in this rewrite:

```json
{"event":"compaction.start","conversationId":"...","reason":"threshold|overflow|manual"}
{"event":"compaction.complete","conversationId":"...","reason":"...","messagesCompacted":42,"tokensBefore":287000,"tokensAfter":24500,"durationMs":1840}
{"event":"compaction.error","conversationId":"...","reason":"...","error":"..."}
```

These flow through the existing Serilog pipeline and are queryable via `/api/logs?search=compaction`. If a future UI wants to render compaction markers, `Conversation.Context` + `CompactedUpToRowId` are already on the conversation object.

## Decisions

All open questions resolved:

| # | Decision |
|---|---|
| **Q1: Tool result storage** | Disk, referenced from SQLite via `Messages.ToolResultRef`. Layout above. File-first write, fail-tolerant read, delete cascades. Inside `SqliteConversationStore`, no separate interface. |
| **Q2: Manual compaction surface** | `POST /api/conversations/{id}/compact` with optional `{"instructions": "..."}` body. No self-compaction tool, no channel slash-command. |
| **Q3: Memories handling** | Drop `memories` from compaction prompt and `CompactionResult`. Compaction and memory are separate lanes; memory pipeline (daily files → indexer → `search_memory` / `load_memory_chunks`) stays untouched. Compaction prompts gain one line requiring the summary to preserve the *content* of memory retrievals, so the post-compaction agent doesn't re-search for things it already knows. |
| **Q4: Per-channel summary style** | One compaction prompt for all conversations (Text or Voice, any `Source`). Revisit only if real-world chat compaction quality is poor. |
| **Q5: Visibility** | Silent + log-only. Structured Info logs per compaction phase. No UI affordance. `Conversation.Context` + `CompactedUpToRowId` remain on the conversation object for any future UI. |

Also locked during the discussion:

- **`keepRecentTokens`** (token budget) replaces `KeepLatestMessagePairs` (count).
- **UI-side `GetMessages` returns only post-cut messages** and never includes the summary inline. Summary injection moves into provider `BuildChatMessages`.
- **`Conversation.Context` and `CompactedUpToRowId`** stay exposed on the conversation object — no new API fields needed for future UI.

## Staged rollout

Three self-contained PRs. Each one is reversible; each one leaves the system in a working state.

### PR 1 — Stop destroying tool results

- Add `Messages.ToolResultRef` column and migration.
- Add `Message.FullToolResult` transient property (`[JsonIgnore]`).
- Add blob helpers to `SqliteConversationStore` (save/read/delete).
- Extend `GetMessages` with optional `includeToolResultBlobs` parameter.
- Both providers (`AzureOpenAiTextProvider`, `AnthropicSubscriptionTextProvider`) write full result to disk via `FullToolResult` on persist; read it back when building chat messages.
- Extend `Delete(conversationId)` to remove the blob directory.
- Mirror changes in `InMemoryConversationStore` for tests.
- No change to compaction, triggers, or the summary-as-system-message behavior.

**Plan:** [`2026-04-24-context-management-rewrite-plan.md`](2026-04-24-context-management-rewrite-plan.md).

### PR 2 — Smarter cut point, user-role summary, per-model window

- Rewrite `RunCompactionAsync` cut logic: token walk, snap to non-tool boundary, `keepRecentTokens`.
- Move summary injection from `GetMessages` (prepends `system`) to provider `BuildChatMessages` (prepends `<summary>`-wrapped `user`).
- UI-side `GetMessages` returns only post-cut messages, no summary prepended.
- Token estimation utility (`TokenEstimator.EstimateMessage`) in `OpenAgent.Compaction`.
- Per-model window plumbing (`Conversation.ContextWindowTokens`, `ILlmTextProvider.GetContextWindow`, threshold becomes window-relative).
- **Tests:** cut never lands on a `tool` role; summary round-trips as user message; threshold scales with active model; voice-conversation cut-point test (many short turns).

### PR 3 — Overflow + manual triggers, iterative summary, cancellation

- Extract `PerformCompactionAsync` from `RunCompactionAsync`. Add `reason` + `customInstructions` parameters.
- Wire `Overflow` in both providers: catch the context-length error (provider-specific — Azure returns `context_length_exceeded`, Anthropic returns a `400` with `context_length` in the body), call compaction synchronously, retry the turn once, surface a clear error if the retry also overflows.
- Expose `POST /api/conversations/{id}/compact` with optional `{"instructions": "..."}` body.
- Split compaction prompts into `Initial` and `Update`, pick based on whether `existingContext` is set. Drop `memories` field. Add memory-retrieval preservation rule.
- Per-conversation `CancellationTokenSource` for threshold runs; scoped critical section around the cutoff swap.
- Structured Info logs: `compaction.start`, `compaction.complete`, `compaction.error`.
- **Tests:** overflow triggers retry; two consecutive overflows surface an error; manual endpoint works; iterative compaction preserves old `[ref: ...]` references.

## Out of Scope / Follow-ups

- **Cap on blob storage.** If SQLite + `tool-results/` growth becomes an issue, add a background job that drops blobs older than N turns or larger than B bytes, setting `ToolResultRef = NULL` so readers fall back to `Content`. Defer until measured.
- **Orphan blob sweep.** A periodic scan matching `tool-results/*.txt` against existing `Messages.ToolResultRef` values, removing unreferenced files. Defer until needed.
- **Image tool results.** Your `ITool.ExecuteAsync` returns `string` today, so N/A. If images are added later, the blob scheme handles binary content unchanged.
- **Memory index integration of compaction output.** Explicitly out of scope — the memory pipeline handles durable facts via daily memory files and the nightly indexer. Compaction is a conversation-context concern only.
- **`ConversationType` → `IsVoice` refactor.** Small, self-contained refactor separate from this rewrite. Touches `Conversation`, `SystemPromptBuilder.FileMap`, `SqliteConversationStore` schema migration, `GetOrCreate` callers, `AgentLogic.GetSystemPrompt` signature. Track as its own issue.
- **Per-channel summary variants.** If real-world Telegram/WhatsApp compaction looks wrong, add `CompactionPromptFor(source)` dispatch. Easy to add later.
- **UI surfacing.** A "Conversation summary" panel showing `Conversation.Context`, or a timeline divider at `CompactedUpToRowId`. Data is already on the conversation object.
