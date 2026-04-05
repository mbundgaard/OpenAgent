using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Skills;

/// <summary>
/// Tool handler providing skill lifecycle tools: activate, deactivate, list, and resource loading.
/// Active skills are stored on the Conversation and injected into the system prompt.
/// </summary>
public sealed class SkillToolHandler : IToolHandler
{
    public IReadOnlyList<ITool> Tools { get; }

    public SkillToolHandler(SkillCatalog catalog, IConversationStore store, ILogger<SkillToolHandler> logger)
    {
        Tools =
        [
            new ActivateSkillTool(catalog, store, logger),
            new DeactivateSkillTool(store),
            new ListActiveSkillsTool(catalog, store),
            new ActivateSkillResourceTool(catalog)
        ];
    }
}

internal sealed class ActivateSkillTool(SkillCatalog catalog, IConversationStore store, ILogger logger) : ITool
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

        logger.LogDebug("activate_skill called: skill={SkillName}, conversation={ConversationId}", name, conversationId);

        if (!catalog.TryGetSkill(name, out _))
        {
            logger.LogWarning("Skill not found in catalog: {SkillName}", name);
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Skill '{name}' not found" }));
        }

        var conversation = store.Get(conversationId);
        if (conversation is null)
        {
            logger.LogWarning("Conversation not found: {ConversationId}", conversationId);
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Conversation not found" }));
        }

        const int maxActiveSkills = 5;

        conversation.ActiveSkills ??= [];
        if (conversation.ActiveSkills.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogDebug("Skill already active: {SkillName} on {ConversationId}", name, conversationId);
            return Task.FromResult(JsonSerializer.Serialize(new { status = "already_active", skill = name }));
        }

        if (conversation.ActiveSkills.Count >= maxActiveSkills)
        {
            logger.LogWarning("Max active skills reached for {ConversationId}: {Count}/{Max}", conversationId, conversation.ActiveSkills.Count, maxActiveSkills);
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Maximum {maxActiveSkills} active skills reached. Deactivate a skill first." }));
        }

        conversation.ActiveSkills.Add(name);
        store.Update(conversation);

        // Verify persistence by re-reading from store
        var persisted = store.Get(conversationId);
        var persistedSkills = persisted?.ActiveSkills ?? [];
        logger.LogInformation("Skill activated: {SkillName} on {ConversationId}, persisted active skills: [{ActiveSkills}]",
            name, conversationId, string.Join(", ", persistedSkills));

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

        var match = conversation.ActiveSkills?.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return Task.FromResult(JsonSerializer.Serialize(new { status = "not_active", skill = name }));

        conversation.ActiveSkills!.Remove(match);

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

        if (!catalog.TryGetSkill(skillName, out var skill))
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Skill '{skillName}' not found" }));

        var skillDir = Path.GetDirectoryName(skill!.Location)!;
        var fullPath = Path.GetFullPath(Path.Combine(skillDir, path));
        if (!fullPath.StartsWith(Path.GetFullPath(skillDir), StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Path is outside skill directory" }));

        if (!File.Exists(fullPath))
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"File not found: {path}" }));

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
