# Memory Background Job (#51) — Implementation Plan

Builds on the design in `docs/memory/DESIGN.md` (Background Job section) and the
instructions in `docs/memory/BACKGROUND.md`. Depends on #19 (Memory digest job)
and #17 (Memory index), both already shipped.

The goal: a self-paced, low-frequency proactive layer. Every ~2 hours during
the day, the agent reads its memory, processes any items the user has flagged,
follows curiosity threads, and occasionally posts a short `[Background]`
message into the main conversation. The intent is to make the agent feel
*present and curious*, not chatty.

---

## What we already have

- `SystemJobRunner` + `ISystemJob` (#19) — cron-driven, state-persisted,
  missed-run recovery. The 15-minute tick is a one-line registration.
- `AgentConfig.MainConversationId` — already in the config, currently used as
  the fallback delivery target for scheduled tasks. We reuse it.
- `BACKGROUND.md` — already shipped as an embedded default, extracted to
  `{dataPath}/BACKGROUND.md` on first run.
- `ScheduledTaskExecutor` — the closest analog to "run an agent turn in a
  conversation with a prompt." Reusable as-is.
- `IConversationStore` — `GetMessages` lets us read the last main-conversation
  message timestamp for the 45-minute gate.

---

## Design decisions

### 1. Where the bg agent runs

The bg agent runs in its **own stable conversation** with `Source = "background"`
and a fixed ID (e.g. `system:background`). Reused across runs.

Rejected alternatives:
- **In the main conversation.** Pollutes main history with the agent's
  internal monologue; the user sees every tool call.
- **Fresh conversation per run.** Closest to the design's "starts fresh"
  phrasing but leaves a graveyard of `bg:{date}` conversations.

The "fresh" property comes from compaction, not from spawning new
conversations. We'll trigger manual compaction at the end of each bg run so
the next run starts with a small history. The user's design note "the files
are the continuity" is honored — the sandbox at `memory/background/` is the
real cross-run memory, the conversation is just scratch space for the current
run.

### 2. How the bg agent posts to main

A new tool `post_to_main(message: string)` in a new project
`OpenAgent.Tools.Background` (or co-located with the job — TBD on naming).
The tool:
- Reads `AgentConfig.MainConversationId`. If unset, returns an error.
- Prepends `[Background] ` to the message.
- Writes an assistant message to the main conversation.
- Invokes `DeliveryRouter.DeliverAsync` so it surfaces on whatever channel the
  main conversation is bound to (Telegram, WhatsApp, app, etc.).

The bg agent should only register this tool — bg-side `file_write`/`shell_exec`
remain available for sandbox work, but the only outbound channel is this one
tool. Keeps the "high bar" rule mechanically obvious.

### 3. The 15-minute tick and the three-condition gate

Registered as an `ISystemJob`:

```csharp
public sealed class BackgroundAgentJob : ISystemJob
{
    public string Name => "background-agent";
    public string Cron => "*/15 6-21 * * *";       // every 15 min, 06:00–22:00
    public string Timezone => "Europe/Copenhagen";
    public Task RunAsync(CancellationToken ct) => _runner.RunIfConditionsMetAsync(ct);
}
```

The cron itself enforces the time-of-day window. The `RunIfConditionsMetAsync`
helper checks the two remaining gates:

- **2+ hours since last bg run.** Read from the `system-jobs.json` entry's
  `lastRunAt`. (Easier than a custom field: it's already there.)
- **45+ minutes since last message in main conv.** Query
  `IConversationStore.GetMessages(MainConversationId)` for the newest message
  and inspect `CreatedAt`.

If any gate fails: log at debug level and exit. The runner's normal nightly
state update happens regardless — we don't want a gated-out tick to update
`lastRunAt`. **Important**: gated-out runs must NOT update lastRunAt, or the
2h gate would reset on every tick. Two options:

- (a) Move the gate check into `SystemJobRunner` itself via an optional
  `ShouldRunAsync()` on `ISystemJob`. Cleanest.
- (b) Have the job throw a sentinel exception that `SystemJobRunner` recognizes
  as "not really a run."
- (c) Track `lastBackgroundRunAt` separately in `AgentConfig` or a new file.

Recommendation: **(a)** — add `Task<bool> ShouldRunAsync(CancellationToken)` to
`ISystemJob` with a default-implementation returning `true`, and have
`SystemJobRunner` skip state update + execution when it returns `false`.

### 4. System prompt for the bg conversation

The bg conversation needs a different prompt than text/voice. Two shapes:

- **Layered:** AGENTS.md + SOUL.md + IDENTITY.md + USER.md + TOOLS.md +
  MEMORY.md + recent logs + BACKGROUND.md appended at the end.
- **Replaced:** IDENTITY.md + USER.md + MEMORY.md + recent logs + BACKGROUND.md
  only — skip AGENTS.md (general conversational instructions) and SOUL.md
  (chat-tone guide).

Recommendation: **layered**, but inject BACKGROUND.md as a high-priority
`<background_mode>` block right before the datetime line. The agent stays
itself, just in a different mode. Matches how voice mode works today
(VOICE.md gets layered in, doesn't replace).

`SystemPromptBuilder.Build` already takes `voice: bool`; add a `source` string
and short-circuit when `source == "background"` to include BACKGROUND.md.
Same plumbing as VOICE.md.

### 5. Per-run inputs (MEMORY.md, last 3 logs, INBOX.md, sandbox files)

`MEMORY.md` and the last 3 daily logs are *already* loaded into the system
prompt for every text conversation via `SystemPromptBuilder`. They come for
free. We only need to add:

- `INBOX.md` content — appended to the user-message kickoff prompt.
- `memory/background/` sandbox files — same.

The user message at run start looks like:

```
[Background run]

<inbox>
{INBOX.md content, or "empty"}
</inbox>

<sandbox>
{file list + first ~100 lines of each, or "empty"}
</sandbox>

Process the inbox if anything is there. Otherwise, follow open threads from
memory and the recent logs. Use post_to_main only if you have something
genuinely worth saying. Update your sandbox before finishing.
```

The agent owns the sandbox via existing file tools — no special handling.

### 6. Inbox intake from the main agent

Per the design TODO in `docs/memory/DESIGN.md`: when the user pastes a link in
the main chat and asks the agent to save it, the *main* agent fetches the
page, summarises it, and appends to `memory/background/INBOX.md`.

This needs only an `AGENTS.md` update — no new tool. The main agent already
has `web_fetch` and `file_append`. We add one short paragraph instructing it
to handle "save this for later" intent by writing to `INBOX.md`.

Defer this to a follow-up PR — the bg loop can start working on whatever the
user manually drops in `INBOX.md` until the main agent learns the move.

---

## Concrete deliverables

### New code

```
src/agent/OpenAgent.BackgroundAgent/
  BackgroundAgentJob.cs              ISystemJob — cron registration
  BackgroundAgentRunner.cs           gate check + agent loop orchestration
  PostToMainTool.cs                   the single outbound capability
  ServiceCollectionExtensions.cs      AddBackgroundAgent()
  OpenAgent.BackgroundAgent.csproj
```

### Changes to existing code

- `OpenAgent.Contracts/ISystemJob.cs` — add `Task<bool> ShouldRunAsync(CancellationToken)`
  default-true.
- `OpenAgent.ScheduledTasks/SystemJobs/SystemJobRunner.cs` — call `ShouldRunAsync`
  before executing; skip both run + state update when false.
- `OpenAgent.Models/Configs/AgentConfig.cs` — none (MainConversationId already
  there).
- `OpenAgent/SystemPromptBuilder.cs` — extend `Build` to accept `source` and
  include BACKGROUND.md when `source == "background"`.
- `OpenAgent/AgentLogic.cs` — pass `source` through `GetSystemPrompt`.
- `OpenAgent/Program.cs` — `AddBackgroundAgent()` next to `AddMemoryDigest()`.

### Tests

- `BackgroundAgentRunnerTests` — gate logic (in-window/out, since-last-run,
  since-last-main-message).
- `PostToMainToolTests` — main-conversation-id missing → error; happy path
  delivers via DeliveryRouter.
- `SystemJobRunnerTests` (extended) — `ShouldRunAsync` returning false skips
  execution AND state update.
- `SystemPromptBuilderTests` — background source loads BACKGROUND.md.

---

## Open questions

1. **Naming.** `BackgroundAgentJob` vs `BackgroundAgentJob` vs `ProactiveAgentJob`?
   The directory `memory/background/` and `BACKGROUND.md` already exist, so
   "memory background" matches. But it's not really about memory — it's an
   autonomous agent loop. Lean toward `BackgroundAgentJob` for consistency,
   open to alternatives.
2. **Should `post_to_main` accept a `quote` or `replyTo` field?** Letting the
   bg agent quote the specific user message it's reacting to could improve
   delivery context, but is extra surface area for v1. Probably defer.
3. **Two consecutive errors → backoff?** `SystemJobRunner` currently just
   reschedules on the normal cron after error. For bg, that's fine — a
   broken run silently retries 2h later. Leave as-is.
4. **Visibility.** Should there be a `GET /api/background-agent/state`
   endpoint showing last run, next eligible run, last `post_to_main` time?
   Useful for debugging proactivity tuning. Likely yes, small.

---

## Build order

1. `ShouldRunAsync` on `ISystemJob` + runner skip logic + test.
2. `PostToMainTool` + test (independent piece, easy to verify).
3. `SystemPromptBuilder` source-aware include of BACKGROUND.md + test.
4. `BackgroundAgentRunner` (gate check + agent loop reusing
   `ScheduledTaskExecutor`) + test.
5. `BackgroundAgentJob` + DI wire-up + Program.cs change.
6. Visibility endpoint.
7. (Follow-up PR) AGENTS.md update for inbox intake on the main agent.

Aim is small commits per step, each independently green.
