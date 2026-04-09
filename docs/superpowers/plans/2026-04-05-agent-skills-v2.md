# Agent Skills v2 — Persistent Activation Model

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework skill activation so active skills are persisted on the conversation and injected into the system prompt (compaction-proof), replace the single `activate_skill` tool with four tools (`activate_skill`, `deactivate_skill`, `list_active_skills`, `activate_skill_resource`), and tag skill resource tool results for safe compaction stripping.

**Architecture:** When the LLM calls `activate_skill`, the skill name is stored on the `Conversation` object (new `ActiveSkills` JSON column). On the next turn, `SystemPromptBuilder.Build` reads the active skills list and appends each skill's SKILL.md body to the system prompt — making it permanent and compaction-proof. Skill resources are loaded via `activate_skill_resource` as regular tool results, tagged so the compactor can safely strip them (the agent can re-request them). The `ITool.ExecuteAsync` interface is extended with a `conversationId` parameter — providers already pass it to `AgentLogic.ExecuteToolAsync`, which now forwards it to the tool. Existing tools ignore it; skill tools use it to modify conversation state via `IConversationStore`.

**Tech Stack:** .NET 10, System.Text.Json, SQLite, xUnit

---

## File Structure

| Action | Path | Responsibility |
|--------|------|----------------|
| Modify | `src/agent/OpenAgent.Contracts/ITool.cs` | Add `conversationId` parameter to `ExecuteAsync` |
| Modify | `src/agent/OpenAgent/AgentLogic.cs` | Pass `conversationId` through to `ITool.ExecuteAsync` |
| Modify | `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs` | Add ignored `conversationId` param |
| Modify | `src/agent/OpenAgent.Tools.FileSystem/FileWriteTool.cs` | Add ignored `conversationId` param |
| Modify | `src/agent/OpenAgent.Tools.FileSystem/FileAppendTool.cs` | Add ignored `conversationId` param |
| Modify | `src/agent/OpenAgent.Tools.FileSystem/FileEditTool.cs` | Add ignored `conversationId` param |
| Modify | `src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs` | Add ignored `conversationId` param |
| Modify | `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs` | Add ignored `conversationId` param |
| Modify | `src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs` | Add ignored `conversationId` param |
| Modify | `src/agent/OpenAgent.Models/Conversations/Conversation.cs` | Add `ActiveSkills` property |
| Modify | `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs` | Persist/read `ActiveSkills` column |
| Modify | `src/agent/OpenAgent.Contracts/IAgentLogic.cs` | Change `GetSystemPrompt` signature to accept active skills |
| Modify | `src/agent/OpenAgent/SystemPromptBuilder.cs` | Accept active skills in `Build`, append skill bodies |
| Rewrite | `src/agent/OpenAgent.Skills/SkillToolHandler.cs` | Four tools: activate, deactivate, list, resource — use `conversationId` param, no `conversation_id` in JSON args |
| Modify | `src/agent/OpenAgent/Program.cs` | Update DI registration for SkillToolHandler (needs IConversationStore) |
| Modify | `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs` | Pass `conversation.ActiveSkills` to GetSystemPrompt |
| Modify | `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs` | Pass `conversation.ActiveSkills` to GetSystemPrompt |
| Modify | `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs` | Pass `conversation.ActiveSkills` to GetSystemPrompt |
| Rewrite | `src/agent/OpenAgent.Tests/Skills/SkillToolHandlerTests.cs` | Tests for all four tools |
| Rewrite | `src/agent/OpenAgent.Tests/Skills/SkillIntegrationTests.cs` | E2E tests for persistent activation model |

---

### Task 0: Extend ITool.ExecuteAsync with conversationId

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/ITool.cs`
- Modify: `src/agent/OpenAgent/AgentLogic.cs`
- Modify: `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs`
- Modify: `src/agent/OpenAgent.Tools.FileSystem/FileWriteTool.cs`
- Modify: `src/agent/OpenAgent.Tools.FileSystem/FileAppendTool.cs`
- Modify: `src/agent/OpenAgent.Tools.FileSystem/FileEditTool.cs`
- Modify: `src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs`
- Modify: `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs`
- Modify: `src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs`

- [ ] **Step 1: Update ITool interface**

In `src/agent/OpenAgent.Contracts/ITool.cs`, change:

```csharp
Task<string> ExecuteAsync(string arguments, CancellationToken ct = default);
```

to:

```csharp
Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default);
```

- [ ] **Step 2: Update AgentLogic to pass conversationId**

In `src/agent/OpenAgent/AgentLogic.cs`, change line 38:

```csharp
return await tool.ExecuteAsync(arguments, ct);
```

to:

```csharp
return await tool.ExecuteAsync(arguments, conversationId, ct);
```

- [ ] **Step 3: Update all existing tool implementations**

Each tool's `ExecuteAsync` signature changes. The `conversationId` parameter is added but not used — these tools don't need it.

For each of these 7 files, change:

```csharp
public async Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
```

to:

```csharp
public async Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
```

And for the two sync tools (ExpandToolHandler.cs ExpandTool, SkillToolHandler.cs ActivateSkillTool — already being rewritten in Task 4), change:

```csharp
public Task<string> ExecuteAsync(string arguments, CancellationToken ct = default)
```

to:

```csharp
public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
```

Files to update:
- `src/agent/OpenAgent.Tools.FileSystem/FileReadTool.cs`
- `src/agent/OpenAgent.Tools.FileSystem/FileWriteTool.cs`
- `src/agent/OpenAgent.Tools.FileSystem/FileAppendTool.cs`
- `src/agent/OpenAgent.Tools.FileSystem/FileEditTool.cs`
- `src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs`
- `src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs`
- `src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs`

- [ ] **Step 4: Build and test**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: All 151 tests pass. The existing tests call `tool.ExecuteAsync(args)` which will need a conversationId — but test callers pass it directly, so they'll need updating too. Check for compilation errors in tests and add `""` as the conversationId argument where needed.

- [ ] **Step 5: Commit**

```bash
git add src/agent/OpenAgent.Contracts/ITool.cs src/agent/OpenAgent/AgentLogic.cs src/agent/OpenAgent.Tools.FileSystem/ src/agent/OpenAgent.Tools.Shell/ShellExecTool.cs src/agent/OpenAgent.Tools.WebFetch/WebFetchTool.cs src/agent/OpenAgent.Tools.Expand/ExpandToolHandler.cs
git commit -m "refactor: add conversationId parameter to ITool.ExecuteAsync"
```

---

### Task 1: Add ActiveSkills to Conversation Model

**Files:**
- Modify: `src/agent/OpenAgent.Models/Conversations/Conversation.cs`

- [ ] **Step 1: Add ActiveSkills property**

Add after the `LastActivity` property (line 87) in `Conversation.cs`:

```csharp
    /// <summary>
    /// Names of skills currently active in this conversation.
    /// Active skill instructions are appended to the system prompt.
    /// </summary>
    [JsonPropertyName("active_skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ActiveSkills { get; set; }
```

- [ ] **Step 2: Verify build**

Run: `cd src/agent && dotnet build OpenAgent.Models`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Models/Conversations/Conversation.cs
git commit -m "feat(skills): add ActiveSkills property to Conversation model"
```

---

### Task 2: Persist ActiveSkills in SQLite

**Files:**
- Modify: `src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs`

- [ ] **Step 1: Add TryAddColumn migration**

In `InitializeDatabase()`, after line 94 (`TryAddColumn(connection, "Messages", "ElapsedMs", "INTEGER");`), add:

```csharp
        TryAddColumn(connection, "Conversations", "ActiveSkills", "TEXT");
```

- [ ] **Step 2: Update ReadConversation to read ActiveSkills**

The `ReadConversation` method reads columns by ordinal position. Add `ActiveSkills` to the SELECT lists and the reader.

In `GetAll()` (line 141), `Get()` (line 155), update the SELECT to append `, ActiveSkills` after `LastActivity`.

In `ReadConversation()`, after the `LastActivity` line (line 452), add:

```csharp
            ActiveSkills = reader.IsDBNull(16) ? null : JsonSerializer.Deserialize<List<string>>(reader.GetString(16))
```

- [ ] **Step 3: Update Update() to write ActiveSkills**

In `Update()`, add `ActiveSkills` to the SET clause and parameters. Add to the SQL:

```
ActiveSkills = @activeSkills
```

Add the parameter:

```csharp
cmd.Parameters.AddWithValue("@activeSkills",
    conversation.ActiveSkills is not null
        ? (object)JsonSerializer.Serialize(conversation.ActiveSkills)
        : DBNull.Value);
```

- [ ] **Step 4: Update GetOrCreate() to write ActiveSkills**

In `GetOrCreate()`, add `ActiveSkills` to the INSERT. After the existing columns, add `, ActiveSkills` to the column list and `, @activeSkills` to the VALUES. Add parameter:

```csharp
cmd.Parameters.AddWithValue("@activeSkills", (object?)null ?? DBNull.Value);
```

- [ ] **Step 5: Run existing tests**

Run: `cd src/agent && dotnet test`
Expected: All 151 tests pass. The new column defaults to NULL, so existing conversations are unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/agent/OpenAgent.ConversationStore.Sqlite/SqliteConversationStore.cs
git commit -m "feat(skills): persist ActiveSkills as JSON column in SQLite"
```

---

### Task 3: Thread ActiveSkills Through GetSystemPrompt

**Files:**
- Modify: `src/agent/OpenAgent.Contracts/IAgentLogic.cs`
- Modify: `src/agent/OpenAgent/AgentLogic.cs`
- Modify: `src/agent/OpenAgent/SystemPromptBuilder.cs`
- Modify: `src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs`
- Modify: `src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs`
- Modify: `src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs`

- [ ] **Step 1: Update IAgentLogic signature**

Change the `GetSystemPrompt` method in `IAgentLogic.cs` from:

```csharp
string GetSystemPrompt(string source, ConversationType type);
```

to:

```csharp
/// <summary>Returns the system-level prompt tailored to the conversation source, type, and active skills.</summary>
string GetSystemPrompt(string source, ConversationType type, IReadOnlyList<string>? activeSkills = null);
```

- [ ] **Step 2: Update AgentLogic implementation**

Change `AgentLogic.GetSystemPrompt` from:

```csharp
public string GetSystemPrompt(string source, ConversationType type)
    => promptBuilder.Build(type);
```

to:

```csharp
public string GetSystemPrompt(string source, ConversationType type, IReadOnlyList<string>? activeSkills = null)
    => promptBuilder.Build(type, activeSkills);
```

- [ ] **Step 3: Update SystemPromptBuilder.Build to accept active skills**

Change the `Build` method signature from:

```csharp
public string Build(ConversationType type)
```

to:

```csharp
public string Build(ConversationType type, IReadOnlyList<string>? activeSkills = null)
```

Then replace the existing skill catalog injection block (the `// Append skill catalog when skills are available` block) with:

```csharp
        // Append skill catalog when skills are available
        var catalogPrompt = _skillCatalog.BuildCatalogPrompt();
        if (catalogPrompt.Length > 0)
        {
            var skillSection = """
                The following skills provide specialized instructions for specific tasks.
                When a task matches a skill's description, call the activate_skill tool
                with the skill's name to load its full instructions. Use deactivate_skill
                to remove a skill when you no longer need it. Use list_active_skills to
                see which skills are currently active. Use activate_skill_resource to load
                supporting files (scripts, references) from an active skill.

                """ + catalogPrompt;
            sections.Add(skillSection);
        }

        // Append active skill bodies — these are permanent in the system prompt
        if (activeSkills is { Count: > 0 })
        {
            foreach (var skillName in activeSkills)
            {
                if (!_skillCatalog.TryGetSkill(skillName, out var skill))
                    continue;

                var resources = _skillCatalog.GetSkillResources(skillName);
                var skillSection = $"<active_skill name=\"{skill!.Name}\">\n{skill.Body}";

                if (resources.Count > 0)
                {
                    skillSection += "\n\n<skill_resources>";
                    foreach (var resource in resources)
                        skillSection += $"\n  <file>{resource}</file>";
                    skillSection += "\n</skill_resources>";
                }

                skillSection += "\n</active_skill>";
                sections.Add(skillSection);
            }
        }
```

- [ ] **Step 4: Add GetSkillResources to SkillCatalog**

Add to `src/agent/OpenAgent.Skills/SkillCatalog.cs`:

```csharp
    /// <summary>
    /// Lists files in scripts/, references/, and assets/ subdirectories for a skill.
    /// Returns paths relative to the skill directory using forward slashes.
    /// </summary>
    public IReadOnlyList<string> GetSkillResources(string skillName, int maxResources = 50)
    {
        if (!_skills.TryGetValue(skillName, out var skill))
            return [];

        var skillDir = Path.GetDirectoryName(skill.Location)!;
        var resources = new List<string>();
        var resourceDirs = new[] { "scripts", "references", "assets" };

        foreach (var subDir in resourceDirs)
        {
            var fullSubDir = Path.Combine(skillDir, subDir);
            if (!Directory.Exists(fullSubDir))
                continue;

            foreach (var file in Directory.GetFiles(fullSubDir, "*", SearchOption.AllDirectories))
            {
                if (resources.Count >= maxResources)
                    break;
                resources.Add(Path.GetRelativePath(skillDir, file).Replace('\\', '/'));
            }
        }

        return resources;
    }
```

- [ ] **Step 5: Update all three providers to pass ActiveSkills**

In `AzureOpenAiTextProvider.cs`, change line 324 from:

```csharp
var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type);
```

to:

```csharp
var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type, conversation.ActiveSkills);
```

In `AnthropicSubscriptionTextProvider.cs`, change line 98 from:

```csharp
var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type);
```

to:

```csharp
var systemPrompt = agentLogic.GetSystemPrompt(conversation.Source, conversation.Type, conversation.ActiveSkills);
```

In `AzureOpenAiVoiceSession.cs`, change line 127 from:

```csharp
Instructions = _agentLogic.GetSystemPrompt(_conversation.Source, _conversation.Type),
```

to:

```csharp
Instructions = _agentLogic.GetSystemPrompt(_conversation.Source, _conversation.Type, _conversation.ActiveSkills),
```

- [ ] **Step 6: Build and test**

Run: `cd src/agent && dotnet build`
Expected: Build succeeded.

Run: `cd src/agent && dotnet test`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/agent/OpenAgent.Contracts/IAgentLogic.cs src/agent/OpenAgent/AgentLogic.cs src/agent/OpenAgent/SystemPromptBuilder.cs src/agent/OpenAgent.Skills/SkillCatalog.cs src/agent/OpenAgent.LlmText.OpenAIAzure/AzureOpenAiTextProvider.cs src/agent/OpenAgent.LlmText.AnthropicSubscription/AnthropicSubscriptionTextProvider.cs src/agent/OpenAgent.LlmVoice.OpenAIAzure/AzureOpenAiVoiceSession.cs
git commit -m "feat(skills): thread ActiveSkills through system prompt pipeline"
```

---

### Task 4: Rewrite SkillToolHandler — Four Tools

**Files:**
- Rewrite: `src/agent/OpenAgent.Skills/SkillToolHandler.cs`

The tool handler now needs `IConversationStore` and `SkillCatalog`. Each tool modifies the conversation's `ActiveSkills` list and persists via `store.Update()`.

- [ ] **Step 1: Rewrite SkillToolHandler**

```csharp
// src/agent/OpenAgent.Skills/SkillToolHandler.cs
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts;

namespace OpenAgent.Skills;

/// <summary>
/// Tool handler providing skill lifecycle tools: activate, deactivate, list, and resource loading.
/// Active skills are stored on the Conversation and injected into the system prompt.
/// </summary>
public sealed class SkillToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public SkillToolHandler(SkillCatalog catalog, IConversationStore store)
    {
        Tools =
        [
            new ActivateSkillTool(catalog, store),
            new DeactivateSkillTool(store),
            new ListActiveSkillsTool(catalog, store),
            new ActivateSkillResourceTool(catalog)
        ];
    }
}

internal sealed class ActivateSkillTool(SkillCatalog catalog, IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "activate_skill",
        Description = "Activate a skill for this conversation. The skill's instructions will be added to the system prompt on the next turn. Call this when a task matches a skill's description from the available skills catalog.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "The skill name from the available_skills catalog" }
            },
            required = new[] { "name" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var name = args.GetProperty("name").GetString()!;

        // Verify skill exists
        if (!catalog.TryGetSkill(name, out _))
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Skill '{name}' not found" }));

        // Load conversation and add skill
        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Conversation not found" }));

        conversation.ActiveSkills ??= [];
        if (conversation.ActiveSkills.Contains(name, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(JsonSerializer.Serialize(new { status = "already_active", skill = name }));

        conversation.ActiveSkills.Add(name);
        store.Update(conversation);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "activated",
            skill = name,
            message = $"Skill '{name}' activated. Its instructions will appear in the system prompt on the next turn."
        }));
    }
}

internal sealed class DeactivateSkillTool(IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "deactivate_skill",
        Description = "Deactivate a skill for this conversation. The skill's instructions will be removed from the system prompt on the next turn.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "The skill name to deactivate" }
            },
            required = new[] { "name" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var name = args.GetProperty("name").GetString()!;

        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Conversation not found" }));

        if (conversation.ActiveSkills is null || !conversation.ActiveSkills.Remove(name))
            return Task.FromResult(JsonSerializer.Serialize(new { status = "not_active", skill = name }));

        store.Update(conversation);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "deactivated",
            skill = name,
            message = $"Skill '{name}' deactivated. Its instructions will be removed from the system prompt on the next turn."
        }));
    }
}

internal sealed class ListActiveSkillsTool(SkillCatalog catalog, IConversationStore store) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_active_skills",
        Description = "List skills currently active in this conversation, with their descriptions.",
        Parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var conversation = store.Get(conversationId);
        if (conversation is null)
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Conversation not found" }));

        var activeSkills = conversation.ActiveSkills ?? [];
        var skills = activeSkills
            .Select(name =>
            {
                catalog.TryGetSkill(name, out var skill);
                return new
                {
                    name,
                    description = skill?.Description ?? "unknown",
                    body_length = skill?.Body.Length ?? 0
                };
            })
            .ToList();

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            active_skills = skills,
            count = skills.Count
        }));
    }
}

internal sealed class ActivateSkillResourceTool(SkillCatalog catalog) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "activate_skill_resource",
        Description = "Load a supporting file (script, reference, asset) from an active skill. The content is returned as a tool result. This result may be stripped during compaction — call again if needed.",
        Parameters = new
        {
            type = "object",
            properties = new
            {
                skill_name = new { type = "string", description = "The active skill name" },
                path = new { type = "string", description = "File path relative to the skill directory (e.g. 'references/checklist.md')" }
            },
            required = new[] { "skill_name", "path" }
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var args = JsonDocument.Parse(arguments).RootElement;
        var skillName = args.GetProperty("skill_name").GetString()!;
        var path = args.GetProperty("path").GetString()!;

        // Verify skill exists in catalog
        if (!catalog.TryGetSkill(skillName, out var skill))
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Skill '{skillName}' not found" }));

        // Resolve against skill directory with path traversal guard
        var skillDir = Path.GetDirectoryName(skill!.Location)!;
        var fullPath = Path.GetFullPath(Path.Combine(skillDir, path));
        if (!fullPath.StartsWith(Path.GetFullPath(skillDir), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Path is outside skill directory" }));

        if (!File.Exists(fullPath))
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"File not found: {path}" }));

        // Guard against huge files
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > 256_000)
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"File too large: {fileInfo.Length} bytes (max 256KB)" }));

        var content = File.ReadAllText(fullPath);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            skill = skillName,
            path,
            content
        }));
    }
}
```

- [ ] **Step 2: Verify build**

Run: `cd src/agent && dotnet build OpenAgent.Skills`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Skills/SkillToolHandler.cs
git commit -m "feat(skills): rewrite SkillToolHandler with four tools — activate, deactivate, list, resource"
```

---

### Task 5: Update DI Registration

**Files:**
- Modify: `src/agent/OpenAgent/Program.cs`

- [ ] **Step 1: Update SkillToolHandler registration**

In `Program.cs`, find the existing SkillToolHandler registration:

```csharp
builder.Services.AddSingleton<IToolHandler>(sp =>
    new SkillToolHandler(sp.GetRequiredService<SkillCatalog>()));
```

Replace with:

```csharp
builder.Services.AddSingleton<IToolHandler>(sp =>
    new SkillToolHandler(
        sp.GetRequiredService<SkillCatalog>(),
        sp.GetRequiredService<IConversationStore>()));
```

- [ ] **Step 2: Build and run all tests**

Run: `cd src/agent && dotnet build && dotnet test`
Expected: Build succeeded. Existing tests that don't touch skill tools pass. The old SkillToolHandlerTests and SkillIntegrationTests will fail since the constructor changed and behavior is different — that's expected, we rewrite those in Task 6.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent/Program.cs
git commit -m "feat(skills): update DI registration for new SkillToolHandler signature"
```

---

### Task 6: Rewrite Skill Tool Tests

**Files:**
- Rewrite: `src/agent/OpenAgent.Tests/Skills/SkillToolHandlerTests.cs`

- [ ] **Step 1: Rewrite SkillToolHandlerTests**

These tests need an `IConversationStore`. Use the existing `InMemoryConversationStore` fake.

```csharp
// src/agent/OpenAgent.Tests/Skills/SkillToolHandlerTests.cs
using OpenAgent.Skills;
using OpenAgent.Tests.Fakes;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests.Skills;

public class SkillToolHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryConversationStore _store;

    public SkillToolHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-tool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new InMemoryConversationStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ActivateSkill_adds_skill_to_conversation()
    {
        CreateSkill("code-review", "Review code.", "# Code Review\nInstructions.");
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill");

        var result = await tool.ExecuteAsync("""{"name": "code-review"}""", "conv1");

        Assert.Contains("activated", result);
        var conversation = _store.Get("conv1")!;
        Assert.Contains("code-review", conversation.ActiveSkills!);
    }

    [Fact]
    public async Task ActivateSkill_returns_already_active()
    {
        CreateSkill("code-review", "Review code.", "Body.");
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill");

        await tool.ExecuteAsync("""{"name": "code-review"}""", "conv1");
        var result = await tool.ExecuteAsync("""{"name": "code-review"}""", "conv1");

        Assert.Contains("already_active", result);
    }

    [Fact]
    public async Task ActivateSkill_returns_error_for_unknown_skill()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test", "test");
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill");

        var result = await tool.ExecuteAsync("""{"name": "nonexistent"}""", "conv1");

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task DeactivateSkill_removes_skill_from_conversation()
    {
        CreateSkill("code-review", "Review code.", "Body.");
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var activate = handler.Tools.First(t => t.Definition.Name == "activate_skill");
        var deactivate = handler.Tools.First(t => t.Definition.Name == "deactivate_skill");

        await activate.ExecuteAsync("""{"name": "code-review"}""", "conv1");
        var result = await deactivate.ExecuteAsync("""{"name": "code-review"}""", "conv1");

        Assert.Contains("deactivated", result);
        var conversation = _store.Get("conv1")!;
        Assert.DoesNotContain("code-review", conversation.ActiveSkills ?? []);
    }

    [Fact]
    public async Task DeactivateSkill_returns_not_active_if_not_found()
    {
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test", "test");
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var deactivate = handler.Tools.First(t => t.Definition.Name == "deactivate_skill");

        var result = await deactivate.ExecuteAsync("""{"name": "unknown"}""", "conv1");

        Assert.Contains("not_active", result);
    }

    [Fact]
    public async Task ListActiveSkills_returns_active_skills()
    {
        CreateSkill("code-review", "Review code.", "Body content.");
        CreateSkill("deploy", "Deploy app.", "Deploy body.");
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var activate = handler.Tools.First(t => t.Definition.Name == "activate_skill");
        var list = handler.Tools.First(t => t.Definition.Name == "list_active_skills");

        await activate.ExecuteAsync("""{"name": "code-review"}""", "conv1");
        await activate.ExecuteAsync("""{"name": "deploy"}""", "conv1");

        var result = await list.ExecuteAsync("""{}""", "conv1");

        Assert.Contains("code-review", result);
        Assert.Contains("deploy", result);
        Assert.Contains("\"count\":2", result);
    }

    [Fact]
    public async Task ActivateSkillResource_reads_file()
    {
        CreateSkill("my-skill", "Does something.", "Body.");
        var scriptsDir = Path.Combine(_tempDir, "my-skill", "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "build.sh"), "#!/bin/bash\necho hello");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill_resource");

        var result = await tool.ExecuteAsync("""{"skill_name": "my-skill", "path": "scripts/build.sh"}""", "conv1");

        Assert.Contains("echo hello", result);
    }

    [Fact]
    public async Task ActivateSkillResource_blocks_path_traversal()
    {
        CreateSkill("my-skill", "Does something.", "Body.");
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill_resource");

        var result = await tool.ExecuteAsync("""{"skill_name": "my-skill", "path": "../../etc/passwd"}""", "conv1");

        Assert.Contains("outside skill directory", result);
    }

    [Fact]
    public void ToolHandler_exposes_four_tools()
    {
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);

        Assert.Equal(4, handler.Tools.Count);
        Assert.Contains(handler.Tools, t => t.Definition.Name == "activate_skill");
        Assert.Contains(handler.Tools, t => t.Definition.Name == "deactivate_skill");
        Assert.Contains(handler.Tools, t => t.Definition.Name == "list_active_skills");
        Assert.Contains(handler.Tools, t => t.Definition.Name == "activate_skill_resource");
    }

    private void CreateSkill(string name, string description, string body)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: {name}
            description: {description}
            ---
            {body}
            """);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillToolHandlerTests" -v minimal`
Expected: All 9 tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/agent/OpenAgent.Tests/Skills/SkillToolHandlerTests.cs
git commit -m "test(skills): rewrite tool handler tests for four-tool model"
```

---

### Task 7: Rewrite Integration Tests

**Files:**
- Rewrite: `src/agent/OpenAgent.Tests/Skills/SkillIntegrationTests.cs`

- [ ] **Step 1: Rewrite integration tests for persistent activation**

```csharp
// src/agent/OpenAgent.Tests/Skills/SkillIntegrationTests.cs
using OpenAgent.Skills;
using OpenAgent.Tests.Fakes;
using OpenAgent.Models.Conversations;

namespace OpenAgent.Tests.Skills;

/// <summary>
/// End-to-end tests verifying the full skill pipeline with persistent activation:
/// discovery -> catalog prompt -> activate on conversation -> skill in system prompt -> deactivate.
/// </summary>
public class SkillIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryConversationStore _store;

    public SkillIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new InMemoryConversationStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Full_lifecycle_activate_appears_in_prompt_deactivate_removed()
    {
        // Create a skill on disk
        CreateSkill("git-workflow", "Manage git branches and PRs.", "# Git Workflow\n\n1. Create branch\n2. Make changes\n3. Open PR");
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var activate = handler.Tools.First(t => t.Definition.Name == "activate_skill");
        var deactivate = handler.Tools.First(t => t.Definition.Name == "deactivate_skill");

        // Catalog prompt has the skill listed
        var catalogPrompt = catalog.BuildCatalogPrompt();
        Assert.Contains("<name>git-workflow</name>", catalogPrompt);

        // Before activation — no active skills in prompt
        var conversation = _store.Get("conv1")!;
        Assert.Null(conversation.ActiveSkills);

        // Activate
        await activate.ExecuteAsync("""{"name": "git-workflow"}""", "conv1");

        // After activation — skill is on the conversation
        conversation = _store.Get("conv1")!;
        Assert.Contains("git-workflow", conversation.ActiveSkills!);

        // Build system prompt with active skills — skill body appears
        var promptBuilder = new SystemPromptBuilder(
            new AgentEnvironment { DataPath = _tempDir },
            catalog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SystemPromptBuilder>.Instance);
        var prompt = promptBuilder.Build(ConversationType.Text, conversation.ActiveSkills);
        Assert.Contains("# Git Workflow", prompt);
        Assert.Contains("<active_skill name=\"git-workflow\">", prompt);

        // Deactivate
        await deactivate.ExecuteAsync("""{"name": "git-workflow"}""", "conv1");

        // After deactivation — skill body no longer in prompt
        conversation = _store.Get("conv1")!;
        var promptAfter = promptBuilder.Build(ConversationType.Text, conversation.ActiveSkills);
        Assert.DoesNotContain("# Git Workflow", promptAfter);
        Assert.DoesNotContain("<active_skill", promptAfter);
    }

    [Fact]
    public async Task Resource_loading_works_for_active_skill()
    {
        CreateSkill("my-skill", "Does something.", "# My Skill\nSee references/guide.md.");
        var refsDir = Path.Combine(_tempDir, "my-skill", "references");
        Directory.CreateDirectory(refsDir);
        File.WriteAllText(Path.Combine(refsDir, "guide.md"), "# Guide\nDetailed reference content.");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store);
        var resource = handler.Tools.First(t => t.Definition.Name == "activate_skill_resource");

        var result = await resource.ExecuteAsync("""{"skill_name": "my-skill", "path": "references/guide.md"}""", "conv1");

        Assert.Contains("Detailed reference content", result);
    }

    private void CreateSkill(string name, string description, string body)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: {name}
            description: {description}
            ---
            {body}
            """);
    }
}
```

- [ ] **Step 2: Run integration tests**

Run: `cd src/agent && dotnet test OpenAgent.Tests --filter "FullyQualifiedName~SkillIntegrationTests" -v minimal`
Expected: 2 tests pass.

- [ ] **Step 3: Run the full test suite**

Run: `cd src/agent && dotnet test`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/agent/OpenAgent.Tests/Skills/SkillIntegrationTests.cs
git commit -m "test(skills): rewrite integration tests for persistent activation model"
```

---

### Task 8: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update the Agent Skills architecture section**

Replace the existing `### Agent Skills` section with:

```markdown
### Agent Skills (agentskills.io specification)
Skills are markdown instruction documents (`SKILL.md`) that teach the agent specialized workflows. Implements the open [Agent Skills](https://agentskills.io/specification) format for cross-client compatibility.
- **Discovery** — `SkillDiscovery` scans `{dataPath}/skills/*/SKILL.md` at startup
- **Catalog** — `SkillCatalog` builds `<available_skills>` XML injected into the system prompt (~50-100 tokens per skill)
- **Persistent activation** — `activate_skill` stores skill name on the `Conversation.ActiveSkills` list. `SystemPromptBuilder.Build` appends active skill bodies to the system prompt — compaction-proof.
- **Four tools** — `activate_skill`, `deactivate_skill`, `list_active_skills`, `activate_skill_resource`
- **Resource loading** — `activate_skill_resource(skill_name, path)` loads files relative to the skill directory. Tool results are ephemeral and safe to strip during compaction.
- **Progressive disclosure** — catalog (always), skill body (when activated, in system prompt), resources (on demand, in tool results)
- Skills are NOT tools — they teach the agent HOW to use existing tools for specific workflows
```

- [ ] **Step 2: Update the Key Design Decisions**

Replace the existing skill-related decisions with:

```markdown
- Skills follow the agentskills.io open spec — YAML frontmatter (name, description required) + markdown body. Compatible with Claude Code, Cursor, VS Code Copilot, and 30+ other clients.
- Active skills are persisted on Conversation.ActiveSkills (JSON column in SQLite) and injected into the system prompt — compaction never removes them.
- Skill resources loaded via activate_skill_resource are ephemeral tool results — the compactor can strip them safely, the agent re-requests if needed.
- GetSystemPrompt takes activeSkills parameter so the system prompt is per-conversation, not just per-type.
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md for persistent skill activation model"
```
