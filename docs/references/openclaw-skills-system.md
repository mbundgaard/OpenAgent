# OpenClaw Skill System - Technical Reference

This document describes how the skill system works in the OpenClaw codebase, based on source code analysis of the `OpenClaw` repository.

---

## Overview

Skills are **markdown-based instruction documents** (`SKILL.md` files) that teach the agent how and when to use specific tools or workflows. They are discovered from multiple directories, filtered by eligibility, serialized into the system prompt, and invocable by both the model and the user via slash commands.

---

## 1. Skill Definition & Contract

**Key file:** `src/agents/skills/skill-contract.ts`

A skill extends the canonical `Skill` interface from the upstream `pi-coding-agent` package:

```typescript
export type Skill = CanonicalSkill & {
  source?: string;
};
```

**Core properties:**
- `name` - Unique identifier (snake_case)
- `description` - One-line description shown to the agent
- `filePath` - Path to SKILL.md file
- `baseDir` - Directory containing the skill
- `disableModelInvocation` - Boolean to exclude from model prompt
- `sourceInfo` - Metadata about origin (scope, origin, base directory)

---

## 2. SKILL.md Format

Each skill is a directory containing a `SKILL.md` file with YAML frontmatter and markdown body.

### Required Frontmatter

```markdown
---
name: skill_name
description: One-line description
---

Instructions for the agent go here...
```

### Full Frontmatter Example

```markdown
---
name: gemini
description: Use Gemini CLI
homepage: https://...
user-invocable: true
disable-model-invocation: false
command-dispatch: tool
command-tool: tool_name
command-arg-mode: raw
metadata:
  {
    "openclaw": {
      "emoji": "icon",
      "skillKey": "alt-key",
      "primaryEnv": "GEMINI_API_KEY",
      "os": ["darwin", "linux"],
      "requires": {
        "bins": ["gemini"],
        "anyBins": ["python3", "python"],
        "env": ["GEMINI_API_KEY"],
        "config": ["browser.enabled"]
      },
      "install": [
        {
          "id": "brew",
          "kind": "brew",
          "formula": "gemini-cli",
          "bins": ["gemini"],
          "label": "Install Gemini CLI (brew)"
        }
      ]
    }
  }
---
```

### Frontmatter Fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | **Required.** Unique skill identifier |
| `description` | string | **Required.** One-line description |
| `homepage` | string | URL for skill homepage |
| `user-invocable` | boolean | Whether user can invoke via slash command (default: true) |
| `disable-model-invocation` | boolean | Exclude from model system prompt |
| `command-dispatch` | `"tool"` | Dispatch directly to a tool |
| `command-tool` | string | Tool name for direct dispatch |
| `command-arg-mode` | `"raw"` | Pass args as-is to tool |
| `metadata` | JSON string | OpenClaw-specific metadata block |

### OpenClaw Metadata Schema

```typescript
type OpenClawSkillMetadata = {
  always?: boolean;           // Always include (skip eligibility gates)
  skillKey?: string;          // Alternate config key
  primaryEnv?: string;        // Primary env var for apiKey
  emoji?: string;             // UI display emoji
  homepage?: string;          // Homepage URL
  os?: string[];              // Platform restrictions: "darwin", "linux", "win32"
  requires?: {
    bins?: string[];          // All must exist on PATH
    anyBins?: string[];       // At least one must exist
    env?: string[];           // Required env vars (process or config)
    config?: string[];        // Config paths that must be truthy
  };
  install?: SkillInstallSpec[];  // Dependency installers
};
```

---

## 3. Skill Loading & Discovery

**Key file:** `src/agents/skills.ts` (core), `src/agents/skills/plugin-skills.ts` (plugin source)

### Load Precedence (Highest to Lowest)

1. **Workspace Skills:** `<workspace>/skills/`
2. **Project Agent Skills:** `<workspace>/.agents/skills/`
3. **Personal Agent Skills:** `~/.agents/skills/`
4. **Managed/Local Skills:** `~/.openclaw/skills/`
5. **Bundled Skills:** Shipped with the OpenClaw package
6. **Plugin Skills:** From enabled plugins (lowest precedence)
7. **Extra Directories:** Via `skills.load.extraDirs` config

When multiple sources provide a skill with the same `name`, the higher-precedence source wins.

### Loading Process

1. `loadWorkspaceSkillEntries()` scans all configured skill roots
2. For each root, finds directories containing a `SKILL.md` file
3. Parses YAML frontmatter via `readSkillFrontmatterSafe()`
4. Extracts `metadata` field (single-line JSON inside YAML)
5. Merges by name using precedence order
6. Returns array of `SkillEntry` objects

### SkillEntry Type

```typescript
type SkillEntry = {
  skill: Skill;                           // Core skill object
  frontmatter: ParsedSkillFrontmatter;    // Raw YAML dict
  metadata?: OpenClawSkillMetadata;       // Parsed openclaw section
  invocation?: SkillInvocationPolicy;     // user-invocable, disable-model-invocation
};
```

### Size Limits

| Limit | Default | Description |
|-------|---------|-------------|
| `maxCandidatesPerRoot` | 300 | Max directories scanned per skill root |
| `maxSkillsLoadedPerSource` | 200 | Max skills per source |
| `maxSkillFileBytes` | 256,000 | Max SKILL.md file size |

### Security

- Symlinks are resolved; real path must stay inside the configured skill root
- Path traversal is blocked via `isPathInside()` checks

---

## 4. Skill Filtering & Eligibility

**Key files:** `src/agents/skills/filter.ts`, `src/agents/skills/config.ts`

### Eligibility Checks (in order)

1. **Explicit disable:** `skills.entries.<name>.enabled: false` in config
2. **Bundled allowlist:** If `skills.allowBundled` is set, bundled skills not on the list are excluded
3. **Runtime eligibility** (`evaluateRuntimeEligibility()`):
   - OS match (if `metadata.openclaw.os` specified)
   - `always: true` skips remaining checks
   - Binary requirements (`requires.bins`, `requires.anyBins`) checked on host
   - Environment variables checked in `process.env` or config
   - Config path truthiness (with defaults like `browser.enabled: true`)

### Agent-Level Filtering

```json5
{
  agents: {
    defaults: {
      skills: ["github", "weather"]   // Shared baseline
    },
    list: [
      { id: "writer" },                       // Inherits defaults
      { id: "docs", skills: ["docs-search"] }  // Replaces defaults entirely
    ]
  }
}
```

- `agents.defaults.skills` applies to all agents unless overridden
- `agents.list[].skills` is a **replacement**, not a merge

---

## 5. Prompt Building & Snapshots

**Key file:** `src/agents/skills.ts`

### Snapshot Type

```typescript
type SkillSnapshot = {
  prompt: string;           // XML for system message
  skills: Array<{
    name: string;
    primaryEnv?: string;
    requiredEnv?: string[];
  }>;
  skillFilter?: string[];   // Normalized agent filter
  resolvedSkills?: Skill[]; // Full skill objects
  version?: number;         // Cache invalidation
};
```

### `buildWorkspaceSkillSnapshot()`

1. Loads all skill entries
2. Filters by agent/config eligibility
3. Removes skills with `disableModelInvocation: true`
4. Compacts paths (e.g., `/Users/alice/...` to `~/...`) to save tokens
5. Builds XML prompt block
6. Applies character and count limits

### Prompt Format

```xml
<available_skills>
  <skill>
    <name>github</name>
    <description>Manage GitHub repositories</description>
    <location>~/.agents/skills/github/SKILL.md</location>
  </skill>
</available_skills>
```

### Prompt Limits

| Limit | Default |
|-------|---------|
| `maxSkillsInPrompt` | 150 |
| `maxSkillsPromptChars` | 30,000 |

### Fallback Strategy

If the full format exceeds the character budget:
1. Try **compact format** (name + location only, no description)
2. If still too large, binary search for the largest prefix that fits
3. Append warning: `"Skills truncated: included X of Y"`

### Token Cost Estimates

- Base overhead: ~195 characters (only when >= 1 skill)
- Per skill: ~97 characters + name + description + path
- ~4 chars/token (varies by model)
- 50 skills ~ 5,000-8,000 tokens

---

## 6. Slash Command Invocation

**Key files:** `src/auto-reply/skill-commands-base.ts`, `src/auto-reply/skill-commands.ts`

### Command Registration

Each skill with `user-invocable: true` (the default) becomes a slash command:
- Command name derived from skill name (sanitized: alphanumeric + underscores, max 32 chars)
- Reserved names (built-in commands) are deduplicated

### SkillCommandSpec

```typescript
type SkillCommandSpec = {
  name: string;              // /skill_name
  skillName: string;         // Original skill name
  description: string;       // Truncated to 100 chars
  dispatch?: {               // Optional tool dispatch
    kind: "tool";
    toolName: string;
    argMode?: "raw";
  };
  sourceFilePath?: string;
};
```

### Invocation Flow

1. User types `/skill_name args` or `/skill <name> args`
2. `resolveSkillCommandInvocation()` looks up matching command spec
3. **Tool-dispatched skills:** Invoke tool directly with `{command, commandName, skillName}`
4. **Regular skills:** Read SKILL.md contents and inject as instructions for the model

---

## 7. Skill Installation

**Key files:** `src/agents/skills-install.ts`, `src/agents/skills-install-download.ts`, `src/agents/skills-install-extract.ts`

### Install Spec

```typescript
type SkillInstallSpec = {
  id?: string;
  kind: "brew" | "node" | "go" | "uv" | "download";
  label?: string;
  bins?: string[];
  os?: string[];
  formula?: string;      // brew
  package?: string;       // node/uv
  module?: string;        // go
  url?: string;           // download
  archive?: string;       // tar.gz | tar.bz2 | zip
  extract?: boolean;
  stripComponents?: number;
  targetDir?: string;
};
```

### Installation Commands by Kind

| Kind | Command |
|------|---------|
| `brew` | `brew install <formula>` |
| `node` | `<npm/pnpm/yarn/bun> install -g <package> --ignore-scripts` |
| `go` | `go install <module>` |
| `uv` | `uv tool install <package>` |
| `download` | Fetch, validate SHA256, extract with staging + merge |

### Install Preference Order

1. Brew (if available and preferred)
2. UV
3. Node
4. Brew (fallback)
5. Go
6. Download

### Safety Measures

- Allowlist patterns validate formula/package/module/URL syntax
- Archive extraction uses staging directory + atomic merge
- SHA256 hash verification for downloads
- Preflight checks: tar list, entry count validation
- Timeout enforcement on all commands
- Security scanning via `scanSkillInstallSource()`

---

## 8. ClawHub Marketplace Integration

**Key files:** `src/agents/skills-clawhub.ts`, `src/cli/skills-cli.ts`

### Origin Tracking

```typescript
type ClawHubSkillOrigin = {
  version: 1;
  registry: string;
  slug: string;
  installedVersion: string;
  installedAt: number;
};
```

Skills installed from ClawHub store origin metadata in `.clawhub/origin.json` within the skill directory, enabling version tracking and updates.

### CLI Commands

| Command | Description |
|---------|-------------|
| `openclaw skills search [query]` | Search ClawHub marketplace |
| `openclaw skills install <slug>` | Install skill from ClawHub |
| `openclaw skills update [slug\|--all]` | Update tracked skills |
| `openclaw skills list [--eligible]` | List local skills |
| `openclaw skills info <name>` | Show skill details |
| `openclaw skills check` | Audit eligibility |

---

## 9. Plugin Skills

**Key file:** `src/agents/skills/plugin-skills.ts`

Plugins can bundle skills by declaring them in `openclaw.plugin.json`:

```json
{
  "skills": ["skills/my-skill"]
}
```

### Resolution Process

1. Load plugin manifest registry
2. Normalize plugin IDs (handle aliases, legacy IDs)
3. Check plugin activation state
4. Validate skill paths stay inside plugin root (`isPathInsideWithRealpath()`)
5. Return resolved skill directories

Plugin skills have the **lowest precedence** and can be overridden by workspace, managed, or bundled skills with the same name.

---

## 10. Environment & Config Injection

**Key files:** `src/agents/skills/env-overrides.ts`, `src/agents/skills/config.ts`

### Per-Skill Config

```json5
{
  skills: {
    entries: {
      "my-skill": {
        enabled: true,
        apiKey: { source: "env", provider: "default", id: "API_KEY" },
        env: {
          API_KEY: "value"
        },
        config: {
          endpoint: "https://...",
          model: "gpt-4"
        }
      }
    }
  }
}
```

### Environment Injection

- **When:** Agent turn starts
- **How:** `applySkillEnvOverrides()` injects env vars from config
- **Scope:** Limited to agent process (not global shell)
- **Safety:**
  - Only inject if env var not already set
  - Block dangerous patterns (OPENSSL_CONF, NODE_OPTIONS, HOST_*)
  - Track injected keys to prevent leakage to child processes
  - Restore original values after run via reference counting

---

## 11. Hot Reload / Watcher

**Key files:** `src/agents/skills/refresh.ts`, `src/agents/skills/refresh-state.ts`

- Watches `SKILL.md` files in all skill roots using chokidar
- Debounce: 250ms (configurable via `skills.load.watchDebounceMs`)
- Ignores: `.git`, `node_modules`, `dist`, `.venv`, `__pycache__`, `.cache`, `build`
- Uses `awaitWriteFinish` to avoid partial file reads
- Each workspace has a snapshot version counter
- `bumpSkillsSnapshotVersion()` increments version and emits change event
- Next agent turn detects stale version and rebuilds snapshot

---

## 12. Security Scanning

**Key file:** `src/security/skill-scanner.ts`

Scans skill directories for potentially dangerous code:

- **File types scanned:** `.js`, `.ts`, `.jsx`, `.tsx`, `.mjs`, `.cjs`, `.mts`, `.cts`
- **Rules:** dangerous-exec, child_process, fs.write, eval, etc.
- **Severity levels:** critical, warn, info
- **Caching:** By file path + mtime + size

```typescript
type SkillScanFinding = {
  ruleId: string;
  severity: SkillScanSeverity;
  file: string;
  line: number;
  message: string;
  evidence: string;
};
```

---

## 13. Gateway & Protocol

**Key files:** `src/gateway/protocol/schema/agents-models-skills.ts`, `src/gateway/server-methods/skills.ts`

Skills are part of the gateway protocol schema, enabling:
- Skill status queries from UI/clients
- ClawHub search/install operations via gateway methods
- Skill snapshot distribution to connected clients
- Remote skill eligibility checks (binary presence on remote nodes)

---

## 14. UI Integration

**Key files:** `ui/src/ui/controllers/skills.ts`, `ui/src/ui/views/skills-grouping.ts`, `ui/src/ui/views/agents-panels-tools-skills.ts`

The web UI provides:
- Skill listing with eligibility status
- Grouping by source (bundled, workspace, managed, plugin)
- Enable/disable toggles
- Install prompts for missing dependencies
- Agent-skill assignment panels

---

## 15. Skill Creator Tooling

**Key directory:** `skills/skill-creator/scripts/`

- `init_skill.py` - Scaffolds a new skill directory with SKILL.md template
- `package_skill.py` - Packages a skill for distribution
- `test_package_skill.py` - Tests the packaging script

---

## 16. Full Lifecycle Summary

```
Create          Discover        Load            Filter
SKILL.md  --->  Scan roots  --> Parse YAML  --> OS/bins/env/config
                                + merge by      + allowlist
                                  precedence    + agent filter

Snapshot        Inject          Invoke          Refresh
Build XML  -->  System     -->  /command   -->  Watcher detects
prompt          prompt          or model        SKILL.md change,
+ cache         for agent       reads file      bumps version
                turn
```

1. **Define** - Developer creates `skills/<name>/SKILL.md` with frontmatter + instructions
2. **Discover** - Gateway scans configured directories at startup and on watch events
3. **Load** - Parses frontmatter, merges by name using precedence
4. **Filter** - Applies OS, binary, env, config, and allowlist checks
5. **Snapshot** - Builds XML prompt block, caches with version counter
6. **Inject** - Adds prompt to system message for each agent turn
7. **Invoke** - Model reads skill file, or user triggers via `/skill_name`
8. **Refresh** - Watcher bumps version on SKILL.md change; next turn rebuilds snapshot

---

## 17. Full Configuration Reference

```json5
{
  skills: {
    // Only allow these bundled skills (others hidden)
    allowBundled: ["gemini", "peekaboo"],

    load: {
      extraDirs: ["~/custom-skills"],  // Additional skill roots
      watch: true,                      // Enable hot reload
      watchDebounceMs: 250              // Debounce interval
    },

    install: {
      preferBrew: true,                 // Prefer brew over other installers
      nodeManager: "npm"                // npm | pnpm | yarn | bun
    },

    limits: {
      maxCandidatesPerRoot: 300,
      maxSkillsLoadedPerSource: 200,
      maxSkillsInPrompt: 150,
      maxSkillsPromptChars: 30000,
      maxSkillFileBytes: 256000
    },

    entries: {
      "skill-name": {
        enabled: true,
        apiKey: "plaintext-or-secret-ref",
        env: { VAR_NAME: "value" },
        config: { customField: "value" }
      }
    }
  },

  agents: {
    defaults: {
      skills: ["github", "weather"]     // Default allowlist for all agents
    },
    list: [
      { id: "writer" },                         // Inherits defaults
      { id: "docs", skills: ["docs-search"] }    // Overrides defaults
    ]
  }
}
```
