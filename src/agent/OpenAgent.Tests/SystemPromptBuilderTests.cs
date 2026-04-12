using Microsoft.Extensions.Logging.Abstractions;
using OpenAgent.Contracts;
using OpenAgent.Models.Configs;
using OpenAgent.Models.Conversations;
using OpenAgent.Skills;

namespace OpenAgent.Tests;

public class SystemPromptBuilderTests
{
    [Fact]
    public void Build_for_Phone_type_includes_phone_etiquette()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openagent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "skills"));
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "AGENTS.md"), "# Agent baseline");
            File.WriteAllText(Path.Combine(tempDir, "PHONE.md"), "# Phone etiquette");
            File.WriteAllText(Path.Combine(tempDir, "VOICE.md"), "# Voice realtime");

            var environment = new AgentEnvironment { DataPath = tempDir };
            var skillCatalog = new SkillCatalog(Path.Combine(tempDir, "skills"));
            var agentConfig = new AgentConfig();
            var builder = new SystemPromptBuilder(
                environment,
                skillCatalog,
                agentConfig,
                NullLogger<SystemPromptBuilder>.Instance);

            var phonePrompt = builder.Build(ConversationType.Phone, activeSkills: []);
            var voicePrompt = builder.Build(ConversationType.Voice, activeSkills: []);
            var textPrompt = builder.Build(ConversationType.Text, activeSkills: []);

            Assert.Contains("Phone etiquette", phonePrompt);
            Assert.Contains("Agent baseline", phonePrompt);
            Assert.DoesNotContain("Voice realtime", phonePrompt);

            Assert.DoesNotContain("Phone etiquette", voicePrompt);
            Assert.DoesNotContain("Phone etiquette", textPrompt);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
