using System.Text.Json;

namespace OpenAgent.Tests;

/// <summary>
/// Seeds required config files in the test data directory before any tests run.
/// </summary>
public class TestSetup
{
    private static bool _initialized;

    public static void EnsureConfigSeeded()
    {
        if (_initialized) return;
        _initialized = true;

        var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/home/data";
        var configDir = Path.Combine(dataDir, "config");
        Directory.CreateDirectory(configDir);

        var agentConfigPath = Path.Combine(configDir, "agent.json");
        if (!File.Exists(agentConfigPath))
        {
            var config = new
            {
                textProvider = "azure-openai-text",
                textModel = "test-model",
                voiceProvider = "azure-openai-voice",
                voiceModel = "test-model",
                compactionProvider = "azure-openai-text",
                compactionModel = "test-model"
            };
            File.WriteAllText(agentConfigPath, JsonSerializer.Serialize(config));
        }
    }
}
