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
        _store.GetOrCreate("conv1", "test", "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
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
        _store.GetOrCreate("conv1", "test", "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill");

        await tool.ExecuteAsync("""{"name": "code-review"}""", "conv1");
        var result = await tool.ExecuteAsync("""{"name": "code-review"}""", "conv1");

        Assert.Contains("already_active", result);
    }

    [Fact]
    public async Task ActivateSkill_returns_error_for_unknown_skill()
    {
        _store.GetOrCreate("conv1", "test", "test", "test");
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill");

        var result = await tool.ExecuteAsync("""{"name": "nonexistent"}""", "conv1");

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task DeactivateSkill_removes_skill_from_conversation()
    {
        CreateSkill("code-review", "Review code.", "Body.");
        _store.GetOrCreate("conv1", "test", "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
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
        _store.GetOrCreate("conv1", "test", "test", "test");
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
        var deactivate = handler.Tools.First(t => t.Definition.Name == "deactivate_skill");

        var result = await deactivate.ExecuteAsync("""{"name": "unknown"}""", "conv1");

        Assert.Contains("not_active", result);
    }

    [Fact]
    public async Task ListActiveSkills_returns_active_skills()
    {
        CreateSkill("code-review", "Review code.", "Body content.");
        CreateSkill("deploy", "Deploy app.", "Deploy body.");
        _store.GetOrCreate("conv1", "test", "test", "test");

        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
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
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill_resource");

        var result = await tool.ExecuteAsync("""{"skill_name": "my-skill", "path": "scripts/build.sh"}""", "conv1");

        Assert.Contains("echo hello", result);
    }

    [Fact]
    public async Task ActivateSkillResource_blocks_path_traversal()
    {
        CreateSkill("my-skill", "Does something.", "Body.");
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
        var tool = handler.Tools.First(t => t.Definition.Name == "activate_skill_resource");

        var result = await tool.ExecuteAsync("""{"skill_name": "my-skill", "path": "../../etc/passwd"}""", "conv1");

        Assert.Contains("outside skill directory", result);
    }

    [Fact]
    public void ToolHandler_exposes_five_tools()
    {
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);

        Assert.Equal(5, handler.Tools.Count);
        Assert.Contains(handler.Tools, t => t.Definition.Name == "activate_skill");
        Assert.Contains(handler.Tools, t => t.Definition.Name == "deactivate_skill");
        Assert.Contains(handler.Tools, t => t.Definition.Name == "list_active_skills");
        Assert.Contains(handler.Tools, t => t.Definition.Name == "activate_skill_resource");
        Assert.Contains(handler.Tools, t => t.Definition.Name == "reload_skills");
    }

    [Fact]
    public async Task Reload_skills_picks_up_new_skill_added_at_runtime()
    {
        CreateSkill("first", "Original skill", "First body");
        var catalog = new SkillCatalog(_tempDir);
        var handler = new SkillToolHandler(catalog, _store, new FakeVoiceSessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillToolHandler>.Instance);
        var reload = handler.Tools.Single(t => t.Definition.Name == "reload_skills");

        // New skill added after catalog was loaded
        CreateSkill("second", "Added at runtime", "Second body");

        var result = await reload.ExecuteAsync("{}", conversationId: "test", CancellationToken.None);

        Assert.Contains("\"total\":2", result);
        Assert.Contains("\"second\"", result);
        Assert.Contains("\"first\"", result);
        Assert.True(catalog.TryGetSkill("second", out _));
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
