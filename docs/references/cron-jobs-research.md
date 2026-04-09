# OpenClaw Cron Jobs & Scheduled Tasks - Research Findings

## 1. Core Data Model

**Key files:** `src/cron/types.ts`, `src/cron/types-shared.ts`

A `CronJob` has this structure:

```typescript
CronJob = {
  id: string
  agentId?: string
  sessionKey?: string
  name: string
  description?: string
  enabled: boolean
  deleteAfterRun?: boolean
  createdAtMs: number
  updatedAtMs: number
  schedule: CronSchedule
  sessionTarget: "main" | "isolated" | "current" | "session:${string}"
  wakeMode: "next-heartbeat" | "now"
  payload: CronPayload
  delivery?: CronDelivery
  failureAlert?: CronFailureAlert | false
  state: CronJobState  // Runtime state, separate from definition
}
```

### Schedule Types

Three scheduling strategies (`CronSchedule`):

| Type | Description | Example |
|------|-------------|---------|
| `at` | One-shot timestamp (ISO 8601 or relative) | `"+20m"`, `"2026-04-10T09:00:00Z"` |
| `every` | Fixed interval in milliseconds with optional anchor | `60000` (every minute) |
| `cron` | Standard CRON expression with timezone + optional stagger | `"0 9 * * *"` (daily at 9am) |

Uses the "croner" library for CRON expression parsing (`src/cron/schedule.ts`).

### Payload Types

Two execution models:

- **`systemEvent`** - Enqueues an event to the main session, runs within the next heartbeat cycle
- **`agentTurn`** - Runs a dedicated isolated agent session with its own prompt, model, tools, and timeout

---

## 2. Storage

**Key file:** `src/cron/store.ts`

- **Location:** `~/.openclaw/cron/jobs.json`
- **Format:** Versioned JSON file with a jobs array
- Persisted after every mutation for durability across restarts
- In-memory serialization cache to avoid redundant writes
- Backup mechanism skips state-only changes to reduce I/O

---

## 3. CRUD Operations & Agent Management

**Key files:** `src/cron/service.ts`, `src/cron/service/ops.ts`, `src/cron/service/jobs.ts`

### Service Methods

| Method | Description |
|--------|-------------|
| `add(input)` | Create new job |
| `update(id, patch)` | Modify existing job |
| `remove(id)` | Delete job |
| `list(opts?)` | List enabled jobs |
| `listPage(opts?)` | Paginated listing with filtering/sorting |
| `run(id, mode?)` | Execute immediately ("due" or "force") |
| `enqueueRun(id, mode?)` | Queue for execution |
| `status()` | Get scheduler status |

### Concurrency & Safety

- All operations wrapped in **mutex locks** (`locked()` from `src/cron/service/locked.ts`) for thread-safe access
- After every mutation: `recomputeNextRuns()` recalculates all next-run times
- Timer re-armed after changes to reflect new schedule
- Events emitted for all mutations (for UI/monitoring)

### Job Creation Details (`src/cron/service/jobs.ts`)

- Auto-generates UUIDs if no ID provided
- Validates and normalizes agent IDs, session keys, payloads
- For "every" jobs, ensures anchor is set at creation time

---

## 4. Scheduling Infrastructure

### Timer Loop (`src/cron/service/timer.ts`)

- `armTimer()` (line ~595) sets up the main event loop using `setTimeout()`
- Checks every minute for due jobs
- Maximum timer delay: 60,000ms (prevents Node.js integer overflow)
- Minimum refire gap: 2,000ms (prevents infinite loops)
- Configurable `maxConcurrentRuns` for parallel job execution

### Staggering / Load Balancing (`src/cron/stagger.ts`)

- Top-of-hour CRON expressions automatically staggered by 0-5 minutes
- **Deterministic offset** based on SHA256 hash of job ID (stable per-job)
- Prevents thundering herd at popular times (e.g., all hourly jobs at :00)
- Can be disabled with `--exact` flag

### Startup Catch-up (`runMissedJobs()`, line ~939)

On service start, detects jobs that should have run while offline:
- Runs due jobs with staggered delays to prevent overwhelming the system
- Configurable via `missedJobStaggerMs` (default: 5s) and `maxMissedJobsPerRestart` (default: 5)
- Clears stale `runningAtMs` markers from interrupted jobs

---

## 5. Execution Pipeline

### Flow

1. Timer fires → finds due jobs
2. **systemEvent** jobs: enqueued to main session via `enqueueSystemEvent()` + optional heartbeat wake
3. **agentTurn** jobs: run in isolated session via `runCronIsolatedAgentTurn()` (`src/cron/isolated-agent/run.ts`)
4. Isolated sessions get:
   - Optional model/auth overrides
   - Configurable thinking, timeout, tool filtering
   - Light context mode (skips workspace bootstrap)
   - Delivery integration (announce or webhook)
5. Task ledger entry created via `createRunningTaskRun()` for audit trail
6. After completion: `applyJobResult()` (line ~384) updates state

### Session Targets

| Target | Behavior |
|--------|----------|
| `main` | Enqueues to main session, runs in next heartbeat |
| `isolated` | Dedicated `cron:<jobId>` session |
| `current` | Bound at job creation time |
| `session:custom-id` | Persistent named session for multi-run workflows |

### Error Handling & Retry

- **Transient errors** detected via regex patterns: `rate_limit`, `overloaded`, `network`, `timeout`, `server_error`
- One-shot jobs retry with **exponential backoff**: 30s → 1m → 5m → 15m → 60m
- Permanent errors or max retries exhausted: job disabled but kept in store
- Consecutive error counter incremented per failure

---

## 6. Channel & Communication Integration

**Key files:** `src/cron/delivery-plan.ts`, `src/cron/delivery.ts`, `src/cron/isolated-agent/delivery-target.ts`

### Delivery Configuration

```typescript
CronDelivery = {
  mode: "none" | "announce" | "webhook"
  channel?: ChannelId | "last"    // e.g., "telegram", "discord", "slack"
  to?: string                     // Recipient/chat ID
  threadId?: string | number      // For threaded channels
  accountId?: string              // For multi-account setups
  bestEffort?: boolean
  failureDestination?: CronFailureDestination
}
```

### Delivery Modes

| Mode | Description |
|------|-------------|
| `announce` | Post to messaging channel via the standard delivery infrastructure |
| `webhook` | HTTP POST to webhook URL with job result payload (10s timeout, supports bearer token) |
| `none` | Internal only, no user-facing delivery |

### Channel Resolution Flow

1. Job specifies `delivery.channel` (e.g., `"telegram"`, `"discord"`, `"slack"`) or uses `"last"` (latest active channel)
2. `resolveDeliveryTarget()` resolves:
   - Channel plugin availability
   - Session-based delivery targets from session store
   - Channel account bindings for multi-account setups
3. Delivery plan computed via `resolveCronDeliveryPlan()` which determines default channel for isolated agent jobs

### Failure Notifications (`src/cron/delivery.ts`)

- Sent via `sendFailureNotificationAnnounce()` when job fails N consecutive times
- Configurable `after` threshold (number of consecutive failures before alerting)
- Alert sent to configured channel/webhook
- **Cooldown** prevents notification spam (default: 1 hour)
- Can be disabled per-job with `failureAlert: false`

---

## 7. API & Gateway Integration

### Gateway Setup (`src/gateway/server-cron.ts`)

`buildGatewayCronService()` (line ~144) wires everything together:

```typescript
new CronService({
  storePath,              // Path to jobs.json
  cronEnabled,            // Feature flag (env: OPENCLAW_SKIP_CRON=1 to disable)
  cronConfig,             // Config from config.yaml
  defaultAgentId,
  resolveSessionStorePath,
  sessionStorePath,
  enqueueSystemEvent,     // Callback for waking main session
  requestHeartbeatNow,    // Callback for immediate heartbeat
  runHeartbeatOnce,       // Run heartbeat with override config
  runIsolatedAgentJob,    // Execute isolated turn
  sendCronFailureAlert    // Send failure notifications
})
```

### RPC Endpoints (`src/gateway/server-methods/cron.ts`)

| Endpoint | Description |
|----------|-------------|
| `cron.list` | Paginated listing with filtering/sorting |
| `cron.status` | Scheduler status |
| `cron.add` | Create new job (validated via protocol schema) |
| `cron.update` | Modify job |
| `cron.remove` | Delete job |
| `cron.run` | Execute job immediately |
| `wake` | Force heartbeat/system event wake |

### Validation (`src/gateway/protocol/schema/cron.ts`)

Complete validation schemas using `@sinclair/typebox`:
- `CronScheduleSchema` - Validates at/every/cron
- `CronPayloadSchema` - Validates systemEvent or agentTurn
- `CronDeliverySchema` - Validates announce/webhook/none modes

---

## 8. CLI Interface

**Key files:** `src/cli/cron-cli/register.ts`, `src/cli/cron-cli/register.cron-add.ts`

### Commands

```
cron status              # Show scheduler status
cron list [--all]        # List jobs (default excludes disabled)
cron add                 # Create new job
cron edit                # Modify existing job
cron delete              # Remove job
cron run                 # Execute job immediately
cron runs                # View execution history
```

### Job Creation Options

| Category | Flags |
|----------|-------|
| Schedule | `--at`, `--every`, `--cron`, `--tz`, `--stagger`, `--exact` |
| Execution | `--session`, `--agent`, `--session-key`, `--wake` |
| Payload (main) | `--system-event` |
| Payload (isolated) | `--message`, `--model`, `--timeout-seconds`, `--thinking`, `--light-context`, `--tools` |
| Delivery | `--announce`, `--channel`, `--to`, `--thread-id`, `--account-id` |
| Failure | `--failure-alert`, `--failure-channel` |

---

## 9. Interesting Architectural Patterns

### State vs Definition Separation
Job definition (immutable metadata) is kept separate from job state (runtime data: `nextRunAtMs`, `lastRunAtMs`, `lastStatus`, `consecutiveErrors`, `lastDeliveryStatus`). This enables safe mutations without losing execution history.

### Deterministic Staggering via Hashing
Uses SHA256 hash of the job ID to create a stable, per-job offset for load distribution. This means the same job always gets the same offset, avoiding thundering herd without any coordination.

### Dual Execution Models
Both main-session (`systemEvent`) and isolated-session (`agentTurn`) paths share a unified delivery system. System events are lightweight (enqueue + heartbeat), while isolated turns spin up full agent sessions.

### Delivery Decoupling
Delivery resolution is completely separate from job execution. The delivery plan is computed independently, enabling testing and flexibility. Channels are resolved through a plugin system supporting multi-account setups.

### Generic Job Base Type (`types-shared.ts`)
Uses TypeScript generics to share base structure across different job types, enabling type-safe specialization.

### Session Reaper
Background task that cleans up stale isolated session files after configurable retention (default: 24h) to prevent disk bloat.

### Configuration Hierarchy
Three levels of config: global config in `.openclaw/config.yaml` (`cron` section), per-job overrides (delivery, failure alerts, payload), and environment variable flag (`OPENCLAW_SKIP_CRON=1`) to disable the scheduler entirely.

---

## 10. Key File Index

| Responsibility | File |
|----------------|------|
| Core types | `src/cron/types.ts`, `src/cron/types-shared.ts` |
| Config types | `src/config/types.cron.ts` |
| Main service | `src/cron/service.ts` |
| CRUD operations | `src/cron/service/ops.ts` |
| Job creation/patching | `src/cron/service/jobs.ts` |
| Timer/execution loop | `src/cron/service/timer.ts` |
| Service state | `src/cron/service/state.ts` |
| Schedule computation | `src/cron/schedule.ts` |
| Staggering | `src/cron/stagger.ts` |
| Normalization | `src/cron/service/normalize.ts` |
| Delivery planning | `src/cron/delivery-plan.ts` |
| Delivery & failure alerts | `src/cron/delivery.ts` |
| Isolated agent execution | `src/cron/isolated-agent/run.ts` |
| Channel resolution | `src/cron/isolated-agent/delivery-target.ts` |
| Storage/persistence | `src/cron/store.ts` |
| Gateway setup | `src/gateway/server-cron.ts` |
| RPC handlers | `src/gateway/server-methods/cron.ts` |
| Validation schemas | `src/gateway/protocol/schema/cron.ts` |
| CLI registration | `src/cli/cron-cli/register.ts` |
| CLI add command | `src/cli/cron-cli/register.cron-add.ts` |
| CLI edit command | `src/cli/cron-cli/register.cron-edit.ts` |
| CLI schedule parsing | `src/cli/cron-cli/schedule-options.ts` |
| Mutex utility | `src/cron/service/locked.ts` |
