# DIGEST.md — Memory Curation

This file governs how the agent consolidates, promotes, and retires memories. It runs nightly as part of the cron job, after indexing. No one is waiting — this is maintenance time.

Runs once per night as a single LLM call — all inputs loaded upfront, no agent loop, no tool use.

---

## Schedule

Nightly, after the index job completes (same cron job, sequential).

---

## What to Read

Every run:
- `MEMORY.md` — current state of core memory
- Last 7 daily logs — raw material to curate from

---

## What to Do

**Promote.**
Look for things in the daily logs that are:
- Recurring across multiple sessions — if it keeps coming up, it belongs in core memory
- Significant decisions or conclusions — things that will matter in future sessions
- Durable facts — about Martin, Muneris, ongoing projects, preferences

**Retire.**
Look for things in `MEMORY.md` that are:
- Outdated — superseded by newer information
- Resolved — questions or open threads that are now closed
- No longer relevant — things that mattered once but don't anymore

---

## Output

Output a single JSON file with edit operations against `MEMORY.md`:

```json
{
  "date": "YYYY-MM-DD",
  "operations": [
    { "action": "add", "section": "Martin", "content": "..." },
    { "action": "update", "section": "Projects", "old": "...", "new": "..." },
    { "action": "remove", "section": "Open Questions", "content": "..." }
  ]
}
```

The system saves the JSON to `memory/digests/YYYY-MM-DD.json` and applies the operations to `MEMORY.md`. If nothing needs to change, output an empty operations array.

---

## How to Modify MEMORY.md

- Add new entries under the relevant section, or create a new section if needed
- Update existing entries in place — don't duplicate
- Remove retired entries cleanly — no tombstones or strikethroughs
- Keep it concise — `MEMORY.md` is always loaded, every line costs context

Sessions never write to `MEMORY.md` directly. Only the digest job does.

---

## What Not to Do

- Don't touch daily logs — they are readonly once closed
- Don't write to `BACKGROUND.md`, `DIGEST.md`, or any other instruction file
- Don't send messages to Martin
- Don't promote something just because it appeared once — recurrence matters
- Don't let `MEMORY.md` grow unbounded — retiring is as important as promoting
