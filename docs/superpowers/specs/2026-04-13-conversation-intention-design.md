# Conversation Intention — Design

**Date:** 2026-04-13
**Status:** Approved, ready for implementation plan

## Goal

Give the agent a single mutable string per conversation — its declared "intention" — that survives compaction and is re-injected into the system prompt every turn.

## Problem

When a long conversation is compacted, the original framing ("we're working on the Telnyx scaffolding plan", "this chat is for Q2 OKR reflection") is summarized into the `Conversation.Context` field as historical fact. That summary records *what happened*, not *what we're trying to do*. The agent loses its through-line and may drift, especially after multiple compactions.

`Context` and `DisplayName` already exist on `Conversation` but neither fills this role: `Context` is post-hoc and factual; `DisplayName` is a UI label set by channel providers from platform metadata.

## Solution

Add an `Intention` field on `Conversation`, set by the agent via a new `set_intention` tool, injected into the system prompt as a small XML-tagged block placed near the end of the prompt for high recency.

## Data model

One nullable string column added to `Conversation` (`OpenAgent.Models/Conversations/Conversation.cs`):

```csharp
/// <summary>
/// Agent-declared one-line summary of what this conversation is for.
/// Survives compaction. Set via the set_intention tool. Null until set.
/// </summary>
[JsonPropertyName("intention")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? Intention { get; set; }
```

**Mutability:** replaceable. The agent can call `set_intention` any number of times; each call overwrites the previous value. No history of past intentions is kept (deferred — see "Future" below).

**Empty-state handling:** null until first set. Setting to empty string clears (column written as NULL).

**Persistence:** SQLite column added via the existing `TryAddColumn("conversations", "intention", "TEXT")` migration helper in `SqliteConversationStore`.

## Tool surface

A new tool project `OpenAgent.Tools.Conversation` (sibling to `Tools.ModelManagement`) exposes one tool through a new `ConversationToolHandler`:

**`set_intention`**
- Parameter: `intention` (string, required) — one or two sentences describing what this conversation is for.
- Behavior: writes to `Conversation.Intention`. Empty string clears (sets the column to NULL).
- Hard cap: 500 characters. If exceeded, the value is truncated server-side and the tool result includes a warning. Defends against the agent dumping a paragraph.
- No `get_intention` tool — the agent always sees the current intention in the system prompt.
- No `clear_intention` tool — empty string handles it.

The tool description teaches the agent when to use it: "Set or update the conversation's intention — a one-or-two-sentence statement of what we're working on. Updates as the goal clarifies. Survives compaction. Skip for casual chats with no clear task."

## System prompt injection

`SystemPromptBuilder.Build` gains a new optional parameter:

```csharp
public string Build(
    ConversationType type,
    IReadOnlyList<string>? activeSkills = null,
    string? intention = null)
```

When `intention` is non-null and non-empty, append a section to `sections` immediately before the current-time line:

```
<conversation_intention>{intention}</conversation_intention>
```

Empty-state: omit the tag entirely — no `<conversation_intention/>` noise when unset.

## Provider integration

Both text providers (`AzureOpenAiTextProvider`, `AnthropicSubscriptionTextProvider`) call `SystemPromptBuilder.Build(...)` per request. Each updates the call to pass `conversation.Intention` alongside `conversation.ActiveSkills`. Voice providers and phone providers also call into the system prompt and receive the same change — voice/phone get the field too, since the cost is one line and the mechanism is uniform across conversation types.

## Tool registration

`ConversationToolHandler` is registered as `IToolHandler` in `Program.cs` alongside the other handlers. It depends on `IConversationStore` (to load and save the conversation) and is otherwise stateless.

## REST API

`GET /api/conversations` and `GET /api/conversations/{conversationId}` automatically include the new field via existing JSON serialization — no endpoint changes required.

## Out of scope (v1)

- **UI for editing the intention.** REST returns the field; no PATCH endpoint, no frontend control. Add later if useful.
- **Auto-derivation from the first user message.** The agent decides when (and whether) to set one.
- **History of past intentions.** Replaceable means the previous value is discarded. Upgrade path: the compactor folds the prior intention into `Context` when intention changes — defer until we observe whether losing prior intentions is actually a problem.

## Testing strategy

xUnit, real SQLite temp files (matches existing `SqliteConversationStoreTests` pattern):

- `SqliteConversationStoreTests`
  - Round-trip a `Conversation` with `Intention` set: save, reload, assert equality.
  - Round-trip with `Intention = null`: column is NULL, deserializes back to null.
  - Schema migration adds `intention` to a pre-existing DB that lacks it.
- `SystemPromptBuilderTests`
  - When `intention` is non-empty, output contains `<conversation_intention>...</conversation_intention>` placed just before the timestamp.
  - When `intention` is null or empty, no `<conversation_intention>` substring appears.
- `SetIntentionToolTests`
  - Call with valid string: conversation is updated in the store; tool result is success.
  - Call with empty string: conversation's `Intention` becomes null; tool result is success.
  - Call with 600-char string: stored value is exactly 500 chars; tool result includes the truncation warning.
  - Call against unknown conversation ID: tool result is failure with a clear message.

## Estimated change footprint

| Area | Files | Lines |
|---|---|---|
| New project `OpenAgent.Tools.Conversation` | csproj, `ConversationToolHandler.cs`, `SetIntentionTool.cs` | ~120 |
| `Conversation` model | 1 file | +6 |
| `SqliteConversationStore` | 1 file | +3 (migration + read/write) |
| `SystemPromptBuilder` | 1 file | +6 (parameter + injection block) |
| Provider call sites | 2–4 files | +1 each |
| Program.cs DI | 1 file | +2 |
| Tests | 3–4 files | ~150 |

## Future / explicit upgrade paths

- **Compactor folds prior intention into `Context`** — when `set_intention` is called and the previous value was non-null, the compactor includes "Previous intention: …" in the next compaction summary. Preserves drift history without bloating the system prompt.
- **User-editable in UI** — add `PATCH /api/conversations/{id}` accepting `{ intention: string | null }` and a small inline editor in the conversation list.
- **Structured intentions** (`{goal, non_goals, open_questions}`) — only if the single-string version proves insufficient in practice.
