using OpenAgent.Installer;

namespace OpenAgent.Tests.Installer;

public class FirewallRuleTests
{
    [Fact]
    public void Add_ComposesNetshAdvfirewallAddRule()
    {
        var runner = new FakeSystemCommandRunner();
        var rule = new FirewallRule(runner);

        rule.Add(ruleName: "OpenAgent", port: 8080);

        Assert.Single(runner.Calls);
        Assert.Equal(
            ("netsh.exe", "advfirewall firewall add rule name=\"OpenAgent\" dir=in action=allow protocol=TCP localport=8080"),
            runner.Calls[0]);
    }

    [Fact]
    public void Remove_ComposesNetshAdvfirewallDeleteRule()
    {
        var runner = new FakeSystemCommandRunner();
        var rule = new FirewallRule(runner);

        rule.Remove(ruleName: "OpenAgent");

        Assert.Single(runner.Calls);
        Assert.Equal(
            ("netsh.exe", "advfirewall firewall delete rule name=\"OpenAgent\""),
            runner.Calls[0]);
    }
}
