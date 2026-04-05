using OpenAgent.Skills;

namespace OpenAgent.Tests.Skills;

public class SkillDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public SkillDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-skills-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Discover_finds_skills_in_subdirectories()
    {
        CreateSkill("code-review", """
            ---
            name: code-review
            description: Review code for bugs and style issues.
            ---
            # Code Review
            Instructions here.
            """);

        CreateSkill("deploy", """
            ---
            name: deploy
            description: Deploy the application to production.
            ---
            # Deploy
            Steps here.
            """);

        var skills = SkillDiscovery.Scan(_tempDir);

        Assert.Equal(2, skills.Count);
        Assert.Contains(skills, s => s.Name == "code-review");
        Assert.Contains(skills, s => s.Name == "deploy");
    }

    [Fact]
    public void Discover_skips_directories_without_skill_md()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "no-skill"));
        File.WriteAllText(Path.Combine(_tempDir, "no-skill", "README.md"), "Not a skill");

        CreateSkill("real-skill", """
            ---
            name: real-skill
            description: A real skill.
            ---
            Body.
            """);

        var skills = SkillDiscovery.Scan(_tempDir);

        Assert.Single(skills);
        Assert.Equal("real-skill", skills[0].Name);
    }

    [Fact]
    public void Discover_skips_skills_with_invalid_frontmatter()
    {
        CreateSkill("bad-skill", """
            ---
            name: bad-skill
            ---
            Body.
            """);

        CreateSkill("good-skill", """
            ---
            name: good-skill
            description: A good skill.
            ---
            Body.
            """);

        var skills = SkillDiscovery.Scan(_tempDir);

        Assert.Single(skills);
        Assert.Equal("good-skill", skills[0].Name);
    }

    [Fact]
    public void Discover_returns_empty_list_when_directory_missing()
    {
        var nonExistent = Path.Combine(_tempDir, "does-not-exist");

        var skills = SkillDiscovery.Scan(nonExistent);

        Assert.Empty(skills);
    }

    [Fact]
    public void Discover_sets_absolute_location_path()
    {
        CreateSkill("my-skill", """
            ---
            name: my-skill
            description: Does something.
            ---
            Body.
            """);

        var skills = SkillDiscovery.Scan(_tempDir);

        Assert.Single(skills);
        Assert.True(Path.IsPathRooted(skills[0].Location));
        Assert.True(skills[0].Location.EndsWith("SKILL.md"), $"Expected location to end with SKILL.md, got: {skills[0].Location}");
    }

    [Fact]
    public void Discover_respects_max_skills_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            CreateSkill($"skill-{i}", $"""
                ---
                name: skill-{i}
                description: Skill number {i}.
                ---
                Body.
                """);
        }

        var skills = SkillDiscovery.Scan(_tempDir, maxSkills: 3);

        Assert.Equal(3, skills.Count);
    }

    private void CreateSkill(string name, string content)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        var lines = content.Split('\n').Select(l => l.TrimStart()).ToArray();
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), string.Join("\n", lines));
    }
}
