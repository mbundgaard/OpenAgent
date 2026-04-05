using OpenAgent.Skills;

namespace OpenAgent.Tests.Skills;

public class SkillCatalogTests : IDisposable
{
    private readonly string _tempDir;

    public SkillCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-catalog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void BuildCatalogPrompt_returns_xml_with_skill_entries()
    {
        CreateSkill("code-review", "Review code for bugs.", "# Code Review\nInstructions.");
        CreateSkill("deploy", "Deploy to production.", "# Deploy\nSteps.");

        var catalog = new SkillCatalog(_tempDir);

        var prompt = catalog.BuildCatalogPrompt();

        Assert.Contains("<available_skills>", prompt);
        Assert.Contains("</available_skills>", prompt);
        Assert.Contains("<name>code-review</name>", prompt);
        Assert.Contains("<name>deploy</name>", prompt);
        Assert.Contains("<description>Review code for bugs.</description>", prompt);
        Assert.Contains("<description>Deploy to production.</description>", prompt);
    }

    [Fact]
    public void BuildCatalogPrompt_returns_empty_string_when_no_skills()
    {
        var catalog = new SkillCatalog(_tempDir);

        var prompt = catalog.BuildCatalogPrompt();

        Assert.Equal("", prompt);
    }

    [Fact]
    public void TryGetSkill_returns_skill_by_name()
    {
        CreateSkill("my-skill", "Does something.", "# My Skill\nBody content.");

        var catalog = new SkillCatalog(_tempDir);

        var found = catalog.TryGetSkill("my-skill", out var skill);

        Assert.True(found);
        Assert.Equal("my-skill", skill!.Name);
        Assert.Equal("# My Skill\nBody content.", skill.Body);
    }

    [Fact]
    public void TryGetSkill_returns_false_for_unknown_name()
    {
        var catalog = new SkillCatalog(_tempDir);

        var found = catalog.TryGetSkill("nonexistent", out var skill);

        Assert.False(found);
        Assert.Null(skill);
    }

    [Fact]
    public void SkillNames_returns_all_discovered_skill_names()
    {
        CreateSkill("alpha", "First.", "Body.");
        CreateSkill("beta", "Second.", "Body.");

        var catalog = new SkillCatalog(_tempDir);

        var names = catalog.SkillNames;

        Assert.Equal(2, names.Count);
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    [Fact]
    public void Reload_picks_up_new_skills()
    {
        var catalog = new SkillCatalog(_tempDir);
        Assert.Empty(catalog.SkillNames);

        CreateSkill("new-skill", "Added later.", "Body.");
        catalog.Reload();

        Assert.Single(catalog.SkillNames);
        Assert.Contains("new-skill", catalog.SkillNames);
    }

    [Fact]
    public void BuildCatalogPrompt_includes_location()
    {
        CreateSkill("my-skill", "Does something.", "Body.");

        var catalog = new SkillCatalog(_tempDir);

        var prompt = catalog.BuildCatalogPrompt();

        Assert.Contains("<location>", prompt);
        Assert.Contains("SKILL.md</location>", prompt);
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
