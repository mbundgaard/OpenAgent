using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent;
using OpenAgent.Contracts;
using OpenAgent.Installer;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Skills;

namespace OpenAgent.Tests;

public class SystemPromptSymlinkBlockTests : IDisposable
{
    private readonly string _tempDir;

    public SystemPromptSymlinkBlockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-sp-sym-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "AGENTS.md"), "# You are the agent.");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir))
            return;

        // Delete junction reparse points before recursive delete — on Windows,
        // Directory.Delete(recursive:true) fails when traversing into junctions.
        foreach (var entry in new DirectoryInfo(_tempDir).GetDirectories("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                entry.Delete();
        }

        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Build_WithSymlinkRoots_IncludesPathConventionsBlock()
    {
        var target = Path.Combine(_tempDir, "_target");
        Directory.CreateDirectory(target);
        new DirectoryLinkCreator(new SystemCommandRunner()).CreateLink(
            Path.Combine(_tempDir, "media"), target);

        var builder = new SystemPromptBuilder(
            new AgentEnvironment { DataPath = _tempDir },
            new SkillCatalog(Path.Combine(_tempDir, "skills"), NullLogger<SkillCatalog>.Instance),
            new AgentConfig(),
            NullLogger<SystemPromptBuilder>.Instance);

        var prompt = builder.Build(ConversationType.Text);

        Assert.Contains("<path_conventions>", prompt);
        Assert.Contains("media/", prompt);
    }

    [Fact]
    public void Build_WithoutSymlinkRoots_OmitsPathConventionsBlock()
    {
        var builder = new SystemPromptBuilder(
            new AgentEnvironment { DataPath = _tempDir },
            new SkillCatalog(Path.Combine(_tempDir, "skills"), NullLogger<SkillCatalog>.Instance),
            new AgentConfig(),
            NullLogger<SystemPromptBuilder>.Instance);

        var prompt = builder.Build(ConversationType.Text);

        Assert.DoesNotContain("<path_conventions>", prompt);
    }
}
