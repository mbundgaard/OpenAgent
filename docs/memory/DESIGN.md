# Memory System Design

A plan for how the agent remembers, retrieves, and acts on information over time.

---

## Overview

Three jobs, three responsibilities:

| Job | What it does | Triggered by | LLM? |
|-----|-------------|--------------|------|
| **Index Job** | Indexes closed daily logs into a searchable vector store | Nightly cron | Single call (summary only) |
| **Digest Job** | Curates core memory — promotes, retires, distills | Nightly cron (after index) | Single call |
| **Background Job** | Autonomous discovery — reads memory, researches, writes to main conversation | Cron every 15 min, runs when conditions met | Yes — agent loop |

---

## The Problem

The agent wakes up fresh every session. The only continuity is what's been written down. Right now that's:
- A core memory file (`MEMORY.md`) — manually curated, grows by accumulation
- Daily logs (`memory/YYYY-MM-DD.md`) — raw session notes, only the last 3 days are loaded

Old daily logs fall out of the load window and become invisible. Nothing is indexed. Nothing is promoted systematically. It works today, but doesn't scale.

---

## Three Concepts

### 1. Index Job

**What:** Indexes closed daily logs into a searchable vector store. Makes old memories findable. No agent loop — pure data processing.

**How it works:**
- Runs nightly
- Finds daily logs older than today that haven't been indexed yet — yesterday's log is closed and readonly, that's the natural trigger
- Generates a summary (single LLM call) and an embedding (Azure OpenAI) for each unindexed log
- Stores both in the SQLite vector index
- Marks the log as indexed

**Index table:**
```sql
CREATE TABLE memory_index (
    id          INTEGER PRIMARY KEY,
    file_path   TEXT NOT NULL UNIQUE,
    summary     TEXT,           -- LLM-generated at index time
    embedding   FLOAT[1536],    -- sqlite-vec format
    indexed_at  DATETIME
);
```

**Stack:**
- Embedding generation: Azure OpenAI (`text-embedding-3-small`)
- Vector storage/search: SQLite + `sqlite-vec`
- Summary generation: single LLM call at index time

---

### 2. Digest Job

**What:** Nightly maintenance. Promotes durable insights to core memory, retires outdated ones. Keeps `MEMORY.md` clean and meaningful over time. Silent — no output to Martin.

**How it works:**
- Runs nightly, after the index job (same cron, sequential)
- Single LLM call — all inputs loaded upfront, no agent loop, no tool use
- Reads, reasons, outputs edits to `MEMORY.md`

**Context:**
| Input | Purpose |
|-------|---------|
| `DIGEST.md` | Instructions — what to do and how |
| `MEMORY.md` | Current state of core memory |
| Last 7 daily logs | Raw material to curate from |

**Output:**
| File | What it contains |
|------|-----------------|
| `memory/digests/YYYY-MM-DD.json` | JSON edit operations — saved for reference and troubleshooting |
| `MEMORY.md` | Result of applying the JSON operations |

The LLM outputs a structured JSON file. The system saves it, then applies the operations to `MEMORY.md`. The JSON files provide a full audit trail of every change ever made to core memory.

```json
{
  "date": "2025-07-14",
  "operations": [
    { "action": "add", "section": "Martin", "content": "..." },
    { "action": "update", "section": "Projects", "old": "...", "new": "..." },
    { "action": "remove", "section": "Open Questions", "content": "..." }
  ]
}
```

**The discipline:**
- Sessions write to daily logs only — never directly to `MEMORY.md`
- Only the digest job touches `MEMORY.md`
- This separation keeps core memory clean and trustworthy

**Governed by `DIGEST.md`** — instruction file defining what to read, how to promote/retire, and what to output.

---

### 3. Background Job

**What:** Autonomous runs that read memory, process the inbox, research open threads, and occasionally write a message directly into the main conversation.

**How it works:**
- Cron runs every 15 minutes and checks three conditions:
  - Current time is between 06:00 and 22:00 (Europe/Copenhagen)
  - 2+ hours since last background run
  - 45+ minutes since last message in main conversation
- All three must be true — otherwise do nothing, try again in 15 min
- Self-regulating. Heavy conversation days = fewer runs. Quiet days = runs freely.
- Full agent loop — can use tools, fetch pages, search memory, take multiple turns

**Inputs:**
| Input | Purpose |
|-------|---------|
| `BACKGROUND.md` | Instructions |
| `MEMORY.md` | Core context |
| Last 3 daily logs | Recent conversation context |
| `INBOX.md` | Items to process |
| `memory/background/` | Agent's own sandbox folder — all files loaded every run |

**Output:**
- `memory/background/` — agent manages this folder freely, reads and writes between runs
- Optionally a `[Background]` message into the main conversation
- Martin can reply and the conversation continues naturally, or ignore it entirely

**The bar for writing to the conversation is high.** Most runs should be silent.

**The `memory/background/` folder:**
The agent owns this folder. It decides how to organize it — what to track, how to structure it. `BACKGROUND.md` instructs the agent to actively manage this folder and keep it clean and purposeful — not let it become a dump. The folder is the agent's continuity between runs.

**Governed by `BACKGROUND.md`** — instruction file that defines what to read, what to do, when to write to the conversation, folder management discipline, and what not to do. Tuned over time based on what's useful.

**`memory/background/INBOX.md`** — a queue. When Martin pastes a link in the main conversation, the agent fetches it, summarises it, and adds it here. The background job owns it from that point — picks items up, researches them over one or more runs, tracks progress in the sandbox, surfaces findings to the main conversation, and removes items when done.

---

## Session Integration

The index job produces a searchable store. Regular agent sessions (like the main chat) can query it on demand using two tools:

```
search_memory(query)    → list of matching file paths + summaries
load_memory_file(path)  → full file contents
```

On a hit, the entire file is loaded into context. If context bloat becomes a real problem later, chunk-level retrieval can be added — but start here.

---

## How They Relate

The index job and digest job are the foundation. Background mode depends on both.

```
Index Job       ←  makes history searchable
Digest Job      ←  keeps core memory clean and trustworthy
Background Job  ←  reads memory, processes inbox, surfaces findings to main conversation
```

Background job reads from the memory system. Its findings surface in the main conversation — from there, Martin's responses become part of the daily log, which feeds the digest job.

---

## Build Order

1. **Index Job** — most concrete, immediately useful, pure data processing
2. **Digest Job** — needs the index to work well; single LLM call, curates core memory
3. **Background Job** — needs both to be actually useful rather than just noisy; full agent loop

---

## Files

### Instruction files (root)
| File | Purpose |
|------|---------|
| `MEMORY.md` | Core memory — always loaded into every session |
| `DIGEST.md` | Instructions for the digest job |
| `BACKGROUND.md` | Instructions for the background job |

### Working files (memory/)
| File | Purpose |
|------|---------|
| `memory/YYYY-MM-DD.md` | Daily logs — raw session notes |
| `memory/background/INBOX.md` | Inbox queue — links Martin flags, owned and managed by background job |
| `memory/digests/YYYY-MM-DD.json` | Digest job output — JSON edit operations, full audit trail |

### Infrastructure
| Component | Purpose |
|-----------|---------|
| SQLite `memory_index` table | Vector index of closed daily logs |

---

## TODO — Before Going Live

- Update `AGENTS.md` to inform the agent about the inbox: when Martin pastes a link and asks to save it, fetch it, summarise it, and append it to `memory/background/INBOX.md`.
