using OpenAgent;

namespace OpenAgent.Tests;

public class DataDirectoryBootstrapTests : IDisposable
{
    private readonly string _tempDir;

    public DataDirectoryBootstrapTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-bootstrap-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Run_OnFreshDirectory_SeedsAgentJsonWithEmptySymlinksBlock()
    {
        DataDirectoryBootstrap.Run(_tempDir);

        var agentJson = File.ReadAllText(Path.Combine(_tempDir, "config", "agent.json"));
        Assert.Equal("{\"symlinks\": {}}", agentJson);
    }

    [Fact]
    public void Run_WithExistingAgentJson_PreservesContent()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var existing = "{\"textProvider\":\"custom\"}";
        File.WriteAllText(Path.Combine(configDir, "agent.json"), existing);

        DataDirectoryBootstrap.Run(_tempDir);

        Assert.Equal(existing, File.ReadAllText(Path.Combine(configDir, "agent.json")));
    }
}
