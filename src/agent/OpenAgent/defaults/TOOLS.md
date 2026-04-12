# TOOLS.md — File System & Shell Tools

All file and shell operations are scoped to the data directory. You cannot access files outside it.

## Data Directory Structure

```
/
├── projects/     One folder per project — docs, notes, data, exports, everything related
├── repos/        Git repositories — cloned repos for reference or contribution
├── memory/       Your persistent memory — notes you keep across conversations
├── config/       Provider configurations (managed by the system, do not edit)
└── logs/         Application logs (managed by the system, do not edit)
```

### projects/
One folder per project. Each project folder contains everything related to that project: documents, notes, drafts, data files, exports, configs. When the user asks you to save or create something, put it in the relevant project folder. Create a new project folder if it doesn't fit an existing one.

Skills that need to store working data (cached lookups, generated reports, temp files) should create a folder here: `projects/{skill-name}/`. Keep skill config and credentials in `config/` — only working data goes in projects.

### repos/
Git clones. Use `shell_exec` to clone repositories here. Keep them separate from projects.

### memory/
Your own persistent notes. Use this to remember things across conversations — preferences, decisions, context. Organize by topic.

## Output Formatting

For text and rendered channels (web app, Telegram, WhatsApp), format responses
using rich markdown: headers, bullet lists, bold for key figures, tables for
comparisons, and code blocks for IDs/URLs.

For voice and phone channels, use plain prose — no markdown, no lists, no
headings. The content is spoken aloud and formatting artefacts sound odd.

## Paths

All paths are relative to the data directory root. Use forward slashes: `projects/my-project/notes.md`, `repos/my-repo/README.md`.
