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

    public SkillToolHandler(
        SkillCatalog catalog,
        IConversationStore store,
        IVoiceSessionManager voiceSessionManager,
        ILogger<SkillToolHandler> logger)
    {
        Tools =
        [
            new ActivateSkillTool(catalog, store, voiceSessionManager, logger),
            new DeactivateSkillTool(store, voiceSessionManager, logger),
            new ListActiveSkillsTool(catalog, store),
            new ActivateSkillResourceTool(catalog),
            new ReloadSkillsTool(catalog, logger)
        ];
    }
}

internal sealed class ActivateSkillTool(SkillCatalog catalog, IConversationStore store, IVoiceSessionManager voiceSessionManager, ILogger logger) : ITool
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

        if (!catalog.TryGetSkill(name, out var entry))
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

        const int maxActiveSkills = 8;

        conversation.ActiveSkills ??= [];
        var alreadyActive = conversation.ActiveSkills.Contains(name, StringComparer.OrdinalIgnoreCase);

        if (!alreadyActive)
        {
            if (conversation.ActiveSkills.Count >= maxActiveSkills)
            {
                logger.LogWarning("Max active skills reached for {ConversationId}: {Count}/{Max}", conversationId, conversation.ActiveSkills.Count, maxActiveSkills);
                return Task.FromResult(JsonSerializer.Serialize(new { error = $"Maximum {maxActiveSkills} active skills reached. Deactivate a skill first." }));
            }

            conversation.ActiveSkills.Add(name);
            store.Update(conversation);
            logger.LogInformation("Skill activated: {SkillName} on {ConversationId}, persisted active skills: [{ActiveSkills}]",
                name, conversationId, string.Join(", ", conversation.ActiveSkills));
        }

        // Voice/realtime sessions lock the system prompt at session start, so a system-prompt
        // rebuild on the next turn won't reach the live session. Deliver the skill body in the
        // tool result instead — it lands in the realtime conversation buffer and the model
        // attends to it like any other fresh turn. Text channels rebuild the system prompt every
        // turn, so the thin status JSON is enough.
        var hasVoiceSession = voiceSessionManager.TryGetSession(conversationId, out _);
        if (hasVoiceSession)
        {
            var body = catalog.ReadSkillBody(name) ?? entry!.Body;
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                status = alreadyActive ? "already_active" : "activated",
                skill = name,
                instructions = body,
                message = $"Skill '{name}' is active. Read and follow the instructions above to complete the user's task."
            }));
        }

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = alreadyActive ? "already_active" : "activated",
            skill = name,
            message = $"Skill '{name}' activated. Its instructions are now in the system prompt."
        }));
    }
}

internal sealed class DeactivateSkillTool(IConversationStore store, IVoiceSessionManager voiceSessionManager, ILogger logger) : ITool
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
        logger.LogInformation("Skill deactivated: {SkillName} on {ConversationId}", name, conversationId);

        // In a live voice session the skill's body was previously delivered as a tool result
        // and still sits in the realtime conversation buffer — call out that those instructions
        // should no longer apply, since the body itself can't be retracted.
        var hasVoiceSession = voiceSessionManager.TryGetSession(conversationId, out _);
        var message = hasVoiceSession
            ? $"Skill '{name}' deactivated. The instructions you previously received for this skill no longer apply — disregard them."
            : $"Skill '{name}' deactivated. Its instructions are no longer in the system prompt.";

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "deactivated",
            skill = name,
            message
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

internal sealed class ReloadSkillsTool(SkillCatalog catalog, ILogger logger) : ITool
{
    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "reload_skills",
        Description = "Re-scan the skills directory to pick up newly added, removed, or edited skills without restarting the agent. Use after the user adds, removes, or edits a SKILL.md file.",
        Parameters = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        }
    };

    public Task<string> ExecuteAsync(string arguments, string conversationId, CancellationToken ct = default)
    {
        var before = catalog.SkillNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        catalog.Reload();
        var after = catalog.SkillNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = after.Except(before, StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = before.Except(after, StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();

        logger.LogInformation("reload_skills: total={Total}, added=[{Added}], removed=[{Removed}]",
            after.Count, string.Join(", ", added), string.Join(", ", removed));

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "reloaded",
            total = after.Count,
            skills = after.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
            added,
            removed
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
