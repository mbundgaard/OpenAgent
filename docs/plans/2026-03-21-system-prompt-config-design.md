# System Prompt Configuration Design

**Goal:** Add a dedicated admin endpoint for reading and editing the 6 core system prompt markdown files. Partial updates supported.

## Current State

- `SystemPromptBuilder` loads 6 md files (AGENTS, SOUL, IDENTITY, USER, TOOLS, VOICE) from `dataPath` at startup and caches them
- Files are read-only after startup — no way to update without restarting
- No admin API surface for system prompt content

## Design

### Endpoint

New endpoints under `/api/admin/system-prompt`, authorized:

**`GET /api/admin/system-prompt`** — returns the current content of all 6 files:

```json
{
  "agents": "...",
  "soul": "...",
  "identity": "...",
  "user": "...",
  "tools": "...",
  "voice": "..."
}
```

Missing files return `null` for their key.

**`POST /api/admin/system-prompt`** — accepts partial updates. Only included keys are written:

```json
{
  "soul": "Updated soul content..."
}
```

For each key present in the request body:
1. Write the content to the corresponding md file in `dataPath`
2. After all writes, call `SystemPromptBuilder.Reload()` to refresh cached content

Returns `204 No Content` on success.

### Key-to-file mapping

| Key | File |
|-----|------|
| `agents` | `AGENTS.md` |
| `soul` | `SOUL.md` |
| `identity` | `IDENTITY.md` |
| `user` | `USER.md` |
| `tools` | `TOOLS.md` |
| `voice` | `VOICE.md` |

### SystemPromptBuilder changes

Add a public `Reload()` method that re-reads all md files from disk, replacing the cached content. Same logic as the existing `LoadFiles()` called in the constructor.

### No IConfigurable

This does not use the `IConfigurable` / `IConfigStore` pattern. The md files are the source of truth, read and written directly. No JSON config file involved.

## Testing

- Unit test: `Reload()` picks up file changes
- Integration test: `GET` returns file contents, `POST` with partial update writes only the specified file and reloads
