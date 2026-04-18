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
        if (!Directory.Exists(_tempDir))
            return;

        // Delete junction reparse points before recursive delete — on Windows,
        // Directory.Delete(recursive:true) fails when traversing into junctions.
        foreach (var entry in new DirectoryInfo(_tempDir).GetDirectories("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                entry.Delete(); // removes the reparse point without following it
        }

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

    [Fact]
    public void Run_WithConfiguredSymlink_CreatesLinkToTarget()
    {
        var target = Path.Combine(_tempDir, "media-target");
        Directory.CreateDirectory(target);
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "agent.json"),
            $"{{\"symlinks\":{{\"media\":\"{target.Replace("\\", "\\\\")}\"}}}}");

        DataDirectoryBootstrap.Run(_tempDir);

        var linkPath = Path.Combine(_tempDir, "media");
        Assert.True(new DirectoryInfo(linkPath).Exists);
        Assert.True((new DirectoryInfo(linkPath).Attributes & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void Run_WithExistingCorrectSymlink_IsIdempotent()
    {
        var target = Path.Combine(_tempDir, "media-target");
        Directory.CreateDirectory(target);
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "agent.json"),
            $"{{\"symlinks\":{{\"media\":\"{target.Replace("\\", "\\\\")}\"}}}}");

        DataDirectoryBootstrap.Run(_tempDir);
        DataDirectoryBootstrap.Run(_tempDir);

        var linkPath = Path.Combine(_tempDir, "media");
        Assert.True(new DirectoryInfo(linkPath).Exists);
    }

    [Fact]
    public void Run_WithRegularDirectoryAtLinkPath_LeavesItUntouched()
    {
        var target = Path.Combine(_tempDir, "media-target");
        Directory.CreateDirectory(target);
        var existingDir = Path.Combine(_tempDir, "media");
        Directory.CreateDirectory(existingDir);
        var marker = Path.Combine(existingDir, "marker.txt");
        File.WriteAllText(marker, "do not delete");

        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "agent.json"),
            $"{{\"symlinks\":{{\"media\":\"{target.Replace("\\", "\\\\")}\"}}}}");

        DataDirectoryBootstrap.Run(_tempDir);

        Assert.True(File.Exists(marker));
        Assert.Equal("do not delete", File.ReadAllText(marker));
    }
}
