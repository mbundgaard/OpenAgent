# DIGEST.md — Memory Curation

This file governs how the agent consolidates, promotes, and retires memories.
It runs nightly as a single LLM call — all inputs loaded upfront, no agent
loop, no tool use. No one is waiting; this is maintenance time.

---

## What to Read

Every run:
- `MEMORY.md` — current state of core memory
- Last 7 daily logs — raw material to curate from

Both are supplied in the user message below this prompt.

---

## What to Do

**Promote.** Look for things in the daily logs that are:
- Recurring across multiple sessions — if it keeps coming up, it belongs in core memory
- Significant decisions or conclusions — things that will matter in future sessions
- Durable facts — about the user, ongoing projects, preferences

**Retire.** Look for things in `MEMORY.md` that are:
- Outdated — superseded by newer information
- Resolved — questions or open threads that are now closed
- No longer relevant — things that mattered once but don't anymore

**Tidy.** Reword bloated entries, merge near-duplicates, keep the file lean.

---

## Output

Output a single JSON object with edit operations against `MEMORY.md`. Do not
include any prose, code fences, or commentary — only the JSON object.

```json
{
  "date": "YYYY-MM-DD",
  "operations": [
    { "action": "add",    "section": "Martin",    "content": "..." },
    { "action": "update", "section": "Projects",  "old": "...", "new": "..." },
    { "action": "remove", "section": "Open Questions", "content": "..." }
  ]
}
```

If nothing needs to change, output `{"date":"YYYY-MM-DD","operations":[]}`.

**Operation semantics:**
- `add` — appends `content` to the named `## section`. Creates the section if missing.
- `update` — finds the exact substring `old` inside the named section and replaces it with `new`. `new` may be an empty string to delete the matched substring.
- `remove` — if `content` is provided, removes that exact substring from the section. If `content` is omitted or empty, removes the entire section.

Section names are matched case-insensitively against the `## heading` text.

---

## Discipline

- Sessions write to daily logs only — never directly to `MEMORY.md`
- Only the digest job touches `MEMORY.md` — this is its sole responsibility
- Keep `MEMORY.md` concise — it is always loaded into every session, every line costs context
- Retiring matters as much as promoting

---

## What Not to Do

- Don't promote something just because it appeared once — recurrence matters
- Don't fabricate facts that aren't in the supplied material
- Don't write conversational output, only the JSON object
- Don't let `MEMORY.md` grow unbounded
