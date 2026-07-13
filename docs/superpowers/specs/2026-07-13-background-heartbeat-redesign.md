# Background Heartbeat Redesign

**Date:** 2026-07-13
**Status:** Approved design, not yet implemented
**Supersedes:** the `system:background-agent` architecture in [2026-05-31-memory-background-job.md](../../plans/2026-05-31-memory-background-job.md)

## Summary

The heartbeat stops being a subsystem and becomes **a nudge**. Every 30 minutes an ephemeral user
message is injected into the **main conversation**. The agent takes a completely normal turn — full
history, all its tools, its real system prompt — and either replies or emits `[]` and goes silent.
The nudge is never persisted. A silent turn evaporates entirely.

Everything else is deleted.

## Problem

The background agent ran every 30 minutes in its own conversation (`system:background-agent`),
perceived nothing but `INBOX.md` and a sandbox listing, and could speak only through `post_to_main`.
On 2026-07-13 it posted six near-identical messages in one day, re-asking the same three questions,
once barging in 16 minutes after a long human conversation it could not see.

The cause was not bad judgment. It was structural blindness:

1. **No perception.** `BuildKickoffPrompt` fed it `INBOX.md` plus a file listing. No tool in the
   registry reads a conversation. It could not see its own prior `[Background]` posts, and it could
   not see Martin's replies.
2. **No memory of speech.** The `[]` suppression discards the whole turn — including runs where
   `post_to_main` had already delivered. The conversation sat at `turns 0`. Every run began as an
   amnesiac and re-derived the same conclusion from the same inputs.
3. **A clock is not a stimulus.** Nothing observable changed between runs, so the only free variable
   was whether to speak. An agent told to be useful, with nothing new to report, speaks anyway.
4. **Duplicate state, no live state.** `memory/background/` held ~6.7k chars of clinical fact that
   already existed in `MEMORY.md`. The facts were everywhere; the state — "I asked this and got no
   answer" — was nowhere.

## Objective

The agent should feel proactive and alive rather than a chatbot: it notices things, works between
conversations, keeps continuity, and occasionally checks in — without spamming.

## Design

### The heartbeat is a nudge

Every 30 minutes, gated on the main conversation being quiet for 15+ minutes, the runner injects an
ephemeral user message into the main conversation — a short reflection prompt sourced from
`BACKGROUND.md`. The agent then takes an ordinary turn.

It is not a different agent. It is the same agent, in the same thread, given a moment to think.

Perception, memory-of-speech, and continuity are not features of this design — they are consequences
of living in the conversation instead of writing letters to it. The agent cannot re-ask a question it
can watch itself having asked.

### Turn persistence

| Part of a heartbeat turn | Fate |
|---|---|
| The nudge | **Never persisted.** Rebuilt each run. |
| Tool rounds and final message | Persisted, exactly like any normal turn. |
| `[]` sentinel | **Whole turn evaporates** — nudge, tool rounds, everything. |

This has a useful property: **silent runs persist nothing**, so they cost the main conversation
nothing. Only a run that actually speaks leaves tool rounds behind — the same footprint as any turn
where Martin had asked a question. The Telegram thread stays a conversation, not a machine log.

Not persisting the nudge follows the existing house rule from CLAUDE.md — *"store everything, compute
the LLM view"* — and mirrors `ReplyQuoteFormatter`, whose output is built at LLM-context-build time
and never persisted.

Mechanically this extends code that already exists: providers track `turnMessageIds` and already call
`DeleteMessages` on the `[]` path.

### Continuity of thoughts: no new mechanism

An earlier draft of this design proposed a `THOUGHTS.md` scratchpad. It is not needed:

- **`MEMORY.md` already has the store.** Its `## Open Questions (raise when the moment fits)` section
  is precisely "considered, not yet surfaced" — currently holding the mood↔T correlation question and
  the sleep-study escalation. The nightly digest already curates it. Building `THOUGHTS.md` would have
  been a second copy of an existing thing.
- **`[]` discards conversation history, not file writes.** A heartbeat can query the DB, think, decide
  not to speak, write what it learned to the daily log, and emit `[]`. The turn vanishes; the file
  survives. `AGENTS.md` already states the principle: *"Write things down. Mental notes don't survive
  session restarts. Files do."*

So the mechanism is: **the agent has file tools. If a thought is worth keeping, it writes it down.**

**Accepted trade-off:** a silent heartbeat's reasoning is thrown away, and the next one re-derives it
from the conversation. This is cheap and correct — the spam was never caused by re-*thinking*, it was
caused by re-*asking* something the agent could not see it had already asked. It can see that now. The
cost is that a slow-burning thought must be consciously written down (daily log → digest →
`MEMORY.md`) rather than parked in a scratchpad automatically.

### Events need no new code

Health-DB changes arrive as short, friendly messages posted into the main conversation via the existing
`POST /api/webhook/conversation/{conversationId}` endpoint — e.g. *"a new workout was added to the
health db"*. That is a real stimulus landing in the thread the agent already lives in, and it runs a
normal turn. No event store, no cooldown-bypass logic.

### No speech cooldown

None is specified. The six-post spiral was caused by blindness, and blindness is fixed. If spam recurs
after this lands, a cooldown bypassed by new events is the fallback — but adding it pre-emptively would
be guarding against a cause we have removed.

## What this deletes

- `post_to_main` tool and `BackgroundToolHandler` — the agent just replies now
- `system:background-agent` conversation and `BackgroundConversationId`
- `memory/background/` — sandbox, `INBOX.md`, thread files
- `BACKGROUND.md`'s inbox-processing and sandbox sections
- The event store, speech cooldown, and `THOUGHTS.md` considered in earlier drafts

The feature gets smaller.

## Fixes folded in

**The starved memory chain.** No daily log has been written since 2026-07-06, so the digest has been
re-reading one stale file for a week and `/api/memory-index/stats` reports 0 chunks. `AGENTS.md` says to
write the daily log *"at the end of every session"* — which never fires for a Telegram conversation that
has no end. The heartbeat is the natural owner: reflection and housekeeping are the same moment, and it
fires every 30 minutes. `BACKGROUND.md` instructs it to keep `memory/YYYY-MM-DD.md` current; the
"end of every session" wording in `AGENTS.md` is replaced. This un-starves digest → `MEMORY.md` → the
memory index.

**The cron window leak.** `SystemJobRunner.ExecuteAsync` returns without advancing `NextRunAt` when a job
is gated out (deliberately, so interval gates aren't reset). A stale past `NextRunAt` therefore keeps the
job permanently due, so it fires whenever the interval gate opens, regardless of the hour — observed
running at 22:54 and 22:30 CPH, outside the `6-21` cron window. Enforce the window so the agent cannot
speak near midnight.

**The accounting hole.** The `[]` check `yield break`s before the usage-stats block, so suppressed turns
never log tokens and never update conversation totals. `system:background-agent` reported
`turns 0, prompt_tokens 0` despite ~16 real runs doing 25–60s of tool work each. Background runs cost real
money and report zero. Log usage before the sentinel check.

## Migration: removing memory/background/

The folder is deleted, but not blind — it held the agent's previous thinking. Its four files were audited
against `MEMORY.md` on 2026-07-13:

| File | Durable content | Covered in MEMORY.md? |
|---|---|---|
| `thyroid_workup_timeline.md` | Timeline, scan discrimination, TRAb/FNA logic, family history | **Yes** — `## Thyroid`, `## Thyroid Workup Timeline` |
| `sleep_osa_thread.md` | Snoring, microsleeps, OSA-upstream hypothesis, sleep study | **Yes** — `## Sleep & Fatigue`, Open Questions #2 |
| `mood_t_correlation_thread.md` | 4.9 vs 32.5 swing, the mood question, confounders | **Yes** — `## Mood`, `## Depression / Mood`, Open Questions #1 |
| `status.md` | Run state, DB audit, week-6 lab plan | **Yes** for the lab plan (`## Key Decisions`); the rest is ephemeral run state |

**Nothing durable is lost by deleting the folder.**

One item in `status.md` is *not* in `MEMORY.md`: the claimed TRT injection-schedule drift (shot 2 logged
10 Jul 19:00 against a planned Thu 9 Jul 18:00; Mon 13 Jul shot unlogged). It is deliberately **not**
promoted, for two reasons: it is an unverified inference the agent drew from DB rows, and `status.md`
contradicts itself about it (calling the same shot both "Wed 10 Jul" and "Fri 10 Jul", and dating shot 3 to
"Mon 6 Jul"). Writing a possibly-wrong claim into health memory is worse than not writing it. It has already
been put to Martin in the thread; it stays a question for him, not a recorded fact.

## Testing

**Unit**

- Gate logic: 30-minute run interval, 15-minute quiet period.
- The nudge is not persisted after a speaking turn.
- `[]` discards the entire turn, including the nudge and tool rounds; the main conversation is
  byte-identical afterwards.
- The cron window is enforced even when ticks are gated out (regression test for the leak).
- Usage is recorded for a suppressed turn.

**Integration**

- A heartbeat run against a main conversation that speaks persists the reply and delivers it through
  `DeliveryRouter`.
- A silent heartbeat run leaves the main conversation unchanged.

## Open items

- Cadence stays 30 min / 15 min quiet. Revisit only if it feels wrong in practice.
- `BackgroundAgentEnabled` is currently `false` in production. Re-enable after this lands.
