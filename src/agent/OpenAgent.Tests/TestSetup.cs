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

        var dataDir = Environment.GetEnvironmentVariable("DATA_DIR");
        if (string.IsNullOrEmpty(dataDir))
        {
            dataDir = "/home/data";
            // Make the host agree with the seed location — RootResolver now falls back to
            // AppContext.BaseDirectory when DATA_DIR is unset, which would put the host
            // on a different path than TestSetup.
            Environment.SetEnvironmentVariable("DATA_DIR", dataDir);
        }
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
