using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Skills;

namespace OpenAgent.Tests;

public class SystemPromptBuilderTests : IDisposable
{
    private readonly string _tempDir;

    public SystemPromptBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-sp-builder-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Build_Voice_IncludesVoiceMd_AndCommonFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "AGENTS.md"), "AGENTS-content");
        File.WriteAllText(Path.Combine(_tempDir, "SOUL.md"), "SOUL-content");
        File.WriteAllText(Path.Combine(_tempDir, "IDENTITY.md"), "IDENTITY-content");
        File.WriteAllText(Path.Combine(_tempDir, "USER.md"), "USER-content");
        File.WriteAllText(Path.Combine(_tempDir, "TOOLS.md"), "TOOLS-content");
        File.WriteAllText(Path.Combine(_tempDir, "MEMORY.md"), "MEMORY-content");
        File.WriteAllText(Path.Combine(_tempDir, "VOICE.md"), "VOICE-content");

        var builder = new SystemPromptBuilder(
            new AgentEnvironment { DataPath = _tempDir },
            new SkillCatalog(Path.Combine(_tempDir, "skills"), NullLogger<SkillCatalog>.Instance),
            new AgentConfig(),
            NullLogger<SystemPromptBuilder>.Instance);

        var prompt = builder.Build("test-conversation-id", voice: true);

        Assert.Contains("AGENTS-content", prompt);
        Assert.Contains("SOUL-content", prompt);
        Assert.Contains("IDENTITY-content", prompt);
        Assert.Contains("USER-content", prompt);
        Assert.Contains("TOOLS-content", prompt);
        Assert.Contains("MEMORY-content", prompt);
        Assert.Contains("VOICE-content", prompt);
    }

    [Fact]
    public void Build_Text_OmitsVoiceMd()
    {
        File.WriteAllText(Path.Combine(_tempDir, "AGENTS.md"), "AGENTS-content");
        File.WriteAllText(Path.Combine(_tempDir, "VOICE.md"), "VOICE-content");

        var builder = new SystemPromptBuilder(
            new AgentEnvironment { DataPath = _tempDir },
            new SkillCatalog(Path.Combine(_tempDir, "skills"), NullLogger<SkillCatalog>.Instance),
            new AgentConfig(),
            NullLogger<SystemPromptBuilder>.Instance);

        var prompt = builder.Build("test-conversation-id", voice: false);

        Assert.Contains("AGENTS-content", prompt);
        Assert.DoesNotContain("VOICE-content", prompt);
    }
}
