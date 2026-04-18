using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public class PreInstallChecksTests : IDisposable
{
    private readonly string _tempDir;

    public PreInstallChecksTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openagent-precheck-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void VerifyBridgeScriptPresent_WithMissingFile_ReturnsError()
    {
        var result = PreInstallChecks.VerifyBridgeScriptPresent(_tempDir);

        Assert.False(result.Ok);
        Assert.Contains("node\\baileys-bridge.js", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyBridgeScriptPresent_WithPresentFile_ReturnsOk()
    {
        var nodeDir = Path.Combine(_tempDir, "node");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "baileys-bridge.js"), "// stub");

        var result = PreInstallChecks.VerifyBridgeScriptPresent(_tempDir);

        Assert.True(result.Ok);
    }

    [Fact]
    public void VerifyNodeAvailable_WithRunnerSuccess_ReturnsOk()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Responses.Enqueue(new CommandResult(0, "v22.10.0"));

        var result = PreInstallChecks.VerifyNodeAvailable(runner);

        Assert.True(result.Ok);
        Assert.Equal(("node", "--version"), runner.Calls[0]);
    }

    [Fact]
    public void VerifyNodeAvailable_WithRunnerFailure_ReturnsError()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Responses.Enqueue(new CommandResult(9009, "'node' is not recognized"));

        var result = PreInstallChecks.VerifyNodeAvailable(runner);

        Assert.False(result.Ok);
        Assert.Contains("node", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyPathSafe_WithNewline_ReturnsError()
    {
        var result = PreInstallChecks.VerifyPathSafe("C:\\Open\nAgent");
        Assert.False(result.Ok);
    }

    [Fact]
    public void VerifyPathSafe_WithSpaces_ReturnsOk()
    {
        var result = PreInstallChecks.VerifyPathSafe(@"C:\Program Files\OpenAgent");
        Assert.True(result.Ok);
    }
}
