using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public class ServiceInstallerTests
{
    [Fact]
    public void Create_ComposesScCreateWithQuoteEscapedBinPath()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.Create(serviceName: "OpenAgent",
            exePath: @"C:\Program Files\OpenAgent\OpenAgent.exe",
            displayName: "OpenAgent",
            description: "Multi-channel AI agent platform");

        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(("sc.exe",
            "create OpenAgent binPath= \"\\\"C:\\Program Files\\OpenAgent\\OpenAgent.exe\\\" --service\" start= auto DisplayName= \"OpenAgent\""),
            runner.Calls[0]);
        Assert.Equal(("sc.exe", "description OpenAgent \"Multi-channel AI agent platform\""),
            runner.Calls[1]);
        Assert.Equal(("sc.exe", "failure OpenAgent reset= 86400 actions= restart/5000/restart/5000/restart/60000"),
            runner.Calls[2]);
    }

    [Fact]
    public void Start_CallsScStart()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.Start("OpenAgent");

        Assert.Single(runner.Calls);
        Assert.Equal(("sc.exe", "start OpenAgent"), runner.Calls[0]);
    }

    [Fact]
    public void Stop_CallsScStop()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.Stop("OpenAgent");

        Assert.Single(runner.Calls);
        Assert.Equal(("sc.exe", "stop OpenAgent"), runner.Calls[0]);
    }

    [Fact]
    public void Delete_CallsScDelete()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.Delete("OpenAgent");

        Assert.Single(runner.Calls);
        Assert.Equal(("sc.exe", "delete OpenAgent"), runner.Calls[0]);
    }

    [Fact]
    public void UpdateBinPath_CallsScConfigWithQuoteEscapedPath()
    {
        var runner = new FakeSystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        installer.UpdateBinPath("OpenAgent", @"C:\OpenAgent\OpenAgent.exe");

        Assert.Single(runner.Calls);
        Assert.Equal(("sc.exe",
            "config OpenAgent binPath= \"\\\"C:\\OpenAgent\\OpenAgent.exe\\\" --service\""),
            runner.Calls[0]);
    }

    [Fact]
    public void IsInstalled_ReturnsTrueOnScQueryExit0()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Responses.Enqueue(new CommandResult(0, "SERVICE_NAME: OpenAgent"));
        var installer = new ServiceInstaller(runner);

        Assert.True(installer.IsInstalled("OpenAgent"));
        Assert.Equal(("sc.exe", "query OpenAgent"), runner.Calls[0]);
    }

    [Fact]
    public void IsInstalled_ReturnsFalseOnScQueryNonZero()
    {
        var runner = new FakeSystemCommandRunner();
        runner.Responses.Enqueue(new CommandResult(1060, "The specified service does not exist"));
        var installer = new ServiceInstaller(runner);

        Assert.False(installer.IsInstalled("OpenAgent"));
    }
}
