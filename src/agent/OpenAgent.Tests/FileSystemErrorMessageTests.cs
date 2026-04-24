using OpenAgent.Installer;
using OpenAgent.Tools.FileSystem;
using System.Text;
using System.Text.Json;

namespace OpenAgent.Tests;

public class FileSystemErrorMessageTests : IDisposable
{
    private readonly string _tempDir;

    public FileSystemErrorMessageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-fs-err-" + Guid.NewGuid());
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
                entry.Delete();
        }

        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task FileRead_OutsideBase_WithSymlinkRoots_IncludesHintsInError()
    {
        var target = Path.Combine(_tempDir, "_target_media");
        Directory.CreateDirectory(target);
        var linkPath = Path.Combine(_tempDir, "media");
        new DirectoryLinkCreator(new SystemCommandRunner()).CreateLink(linkPath, target);

        var tool = new FileReadTool(_tempDir, Encoding.UTF8);
        var result = await tool.ExecuteAsync("""{"path":"../escape.txt"}""", "conv-1");

        using var doc = JsonDocument.Parse(result);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("media", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileRead_OutsideBase_WithoutSymlinkRoots_RetainsGenericError()
    {
        var tool = new FileReadTool(_tempDir, Encoding.UTF8);
        var result = await tool.ExecuteAsync("""{"path":"../escape.txt"}""", "conv-1");

        using var doc = JsonDocument.Parse(result);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
    }
}
