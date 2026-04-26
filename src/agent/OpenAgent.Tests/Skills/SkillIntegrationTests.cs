using OpenAgent.Skills;
using OpenAgent.Tests.Fakes;
using OpenAgent.Models.Conversations;
using OpenAgent.Contracts;

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
        CreateSkill("git-workflow", "Manage git branches and PRs.", "# Git Workflow\n\n1. Create branch\n2. Make changes\n3. Open PR");
        _store.GetOrCreate("conv1", "test", ConversationType.Text, "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
        var activate = handler.Tools.First(t => t.Definition.Name == "activate_skill");
        var deactivate = handler.Tools.First(t => t.Definition.Name == "deactivate_skill");

        // Catalog prompt has the skill listed
        var catalogPrompt = catalog.BuildCatalogPrompt();
        Assert.Contains("<name>git-workflow</name>", catalogPrompt);

        // Before activation — no active skills
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
            new OpenAgent.Models.Configs.AgentConfig(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SystemPromptBuilder>.Instance);
        var prompt = promptBuilder.Build(conversation.Id, ConversationType.Text, conversation.ActiveSkills);
        Assert.Contains("# Git Workflow", prompt);
        Assert.Contains("<active_skill name=\"git-workflow\" directory=", prompt);

        // Deactivate
        await deactivate.ExecuteAsync("""{"name": "git-workflow"}""", "conv1");

        // After deactivation — skill body no longer in prompt
        conversation = _store.Get("conv1")!;
        var promptAfter = promptBuilder.Build(conversation.Id, ConversationType.Text, conversation.ActiveSkills);
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
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
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
