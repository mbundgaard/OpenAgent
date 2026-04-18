namespace OpenAgent.Installer;

/// <summary>
/// Wraps sc.exe for creating, configuring, starting, stopping, querying, and deleting
/// the Windows service. The binPath argument requires the exe path to be wrapped in
/// escaped quotes so paths containing spaces (e.g. C:\Program Files\OpenAgent\...)
/// survive the sc parser.
/// </summary>
public sealed class ServiceInstaller
{
    private readonly ISystemCommandRunner _runner;

    public ServiceInstaller(ISystemCommandRunner runner) => _runner = runner;

    public void Create(string serviceName, string exePath, string displayName, string description)
    {
        var binPath = $"\\\"{exePath}\\\" --service";
        Run($"create {serviceName} binPath= \"{binPath}\" start= auto DisplayName= \"{displayName}\"");
        Run($"description {serviceName} \"{description}\"");
        Run($"failure {serviceName} reset= 86400 actions= restart/5000/restart/5000/restart/60000");
    }

    public void Start(string serviceName) => Run($"start {serviceName}");
    public void Stop(string serviceName) => Run($"stop {serviceName}");
    public void Delete(string serviceName) => Run($"delete {serviceName}");

    public void UpdateBinPath(string serviceName, string exePath)
    {
        var binPath = $"\\\"{exePath}\\\" --service";
        Run($"config {serviceName} binPath= \"{binPath}\"");
    }

    public bool IsInstalled(string serviceName)
    {
        var result = _runner.Run("sc.exe", $"query {serviceName}");
        return result.ExitCode == 0;
    }

    private CommandResult Run(string args) => _runner.Run("sc.exe", args);
}
