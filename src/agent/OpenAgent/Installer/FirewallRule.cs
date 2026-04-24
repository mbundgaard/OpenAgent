namespace OpenAgent.Installer;

/// <summary>
/// Manages the inbound Windows Firewall rule that exposes the HTTP port to the LAN.
/// Opt-in: only invoked when --install is run with --open-firewall-port.
/// </summary>
public sealed class FirewallRule
{
    private readonly ISystemCommandRunner _runner;

    public FirewallRule(ISystemCommandRunner runner) => _runner = runner;

    public void Add(string ruleName, int port) =>
        _runner.Run("netsh.exe",
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}");

    public void Remove(string ruleName) =>
        _runner.Run("netsh.exe",
            $"advfirewall firewall delete rule name=\"{ruleName}\"");
}
