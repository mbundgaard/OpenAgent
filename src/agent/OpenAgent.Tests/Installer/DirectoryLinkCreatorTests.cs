using System.Runtime.InteropServices;
using OpenAgent.Installer;
using Xunit.Sdk;

namespace OpenAgent.Tests.Installer;

public class SkipOnWindowsAttribute : FactAttribute
{
    public SkipOnWindowsAttribute(string skipReason = "Test skipped on Windows")
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = skipReason;
    }
}

public class DirectoryLinkCreatorTests : IDisposable
{
    private readonly string _tempDir;

    public DirectoryLinkCreatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-link-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void CreateLink_WithWindowsOs_ComposesMklinkJunctionCommand()
    {
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: true);
        var linkPath = Path.Combine(_tempDir, "media");
        var targetPath = @"D:\Media";

        creator.CreateLink(linkPath, targetPath);

        Assert.Single(runner.Calls);
        var (exe, args) = runner.Calls[0];
        Assert.Equal("cmd.exe", exe);
        Assert.Equal($"/c mklink /J \"{linkPath}\" \"{targetPath}\"", args);
    }

    [SkipOnWindows("Directory.CreateSymbolicLink requires admin or Developer Mode on Windows; Linux CI covers this branch.")]
    public void CreateLink_WithLinuxOs_CreatesRealSymlink()
    {
        var target = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(target);
        var linkPath = Path.Combine(_tempDir, "link");
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: false);

        creator.CreateLink(linkPath, target);

        Assert.Empty(runner.Calls);
        Assert.True(new DirectoryInfo(linkPath).LinkTarget == target
                    || new DirectoryInfo(linkPath).ResolveLinkTarget(true)?.FullName == new DirectoryInfo(target).FullName);
    }

    [SkipOnWindows("Directory.CreateSymbolicLink requires admin or Developer Mode on Windows; Linux CI covers this branch.")]
    public void LinkExists_OnExistingJunction_ReturnsTrue()
    {
        var target = Path.Combine(_tempDir, "t");
        Directory.CreateDirectory(target);
        var linkPath = Path.Combine(_tempDir, "l");
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: false);
        creator.CreateLink(linkPath, target);

        Assert.True(creator.LinkExists(linkPath));
    }

    [Fact]
    public void LinkExists_OnMissingPath_ReturnsFalse()
    {
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: false);

        Assert.False(creator.LinkExists(Path.Combine(_tempDir, "nope")));
    }

    [SkipOnWindows("Directory.CreateSymbolicLink requires admin or Developer Mode on Windows; Linux CI covers this branch.")]
    public void ReadLinkTarget_OnSymlink_ReturnsTargetPath()
    {
        var target = Path.Combine(_tempDir, "t");
        Directory.CreateDirectory(target);
        var linkPath = Path.Combine(_tempDir, "l");
        var runner = new FakeSystemCommandRunner();
        var creator = new DirectoryLinkCreator(runner, isWindows: false);
        creator.CreateLink(linkPath, target);

        var resolved = creator.ReadLinkTarget(linkPath);

        Assert.Equal(target, resolved);
    }
}
