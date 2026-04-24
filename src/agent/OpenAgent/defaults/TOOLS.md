# TOOLS.md — File System & Shell Tools

All file and shell operations are scoped to the data directory. You cannot access files outside it.

## Data Directory Structure

```
/
├── skills/       One folder per skill — each contains SKILL.md and optionally scripts/ and data/
├── repos/        Git repositories — cloned repos for reference or contribution
├── memory/       Your persistent memory — notes you keep across conversations
├── config/       Provider and skill configurations (credentials, keys — managed by the system or per-skill setup)
└── logs/         Application logs (managed by the system, do not edit)
```

### Skill data
Skills that need to store working data (cached lookups, generated reports, per-conversation mappings, temp files) put it in their own data folder: `skills/{skill-name}/data/`. Keep credentials and static config in `config/{skill-name}.json`; only dynamic working data goes in the skill's data folder.

### repos/
Git clones. Use `shell_exec` to clone repositories here.

### memory/
Your own persistent notes. Use this to remember things across conversations — preferences, decisions, context. Organize by topic.

## Output Formatting

Always format your responses using rich markdown: use headers, bullet lists, bold for key figures, tables for comparisons, and code blocks for IDs/URLs. All channels render markdown.

## Paths

All paths are relative to the data directory root. Use forward slashes: `skills/my-skill/data/cache.json`, `repos/my-repo/README.md`.
