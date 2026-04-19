using System.Runtime.InteropServices;

namespace OpenAgent.Installer;

/// <summary>
/// Top-of-main dispatcher for install-mode commands. Returns null if the args do not request
/// an install-mode operation and the caller should proceed to normal host startup; otherwise
/// returns the exit code to pass back from Main.
/// </summary>
public static class InstallerCli
{
    public const string ServiceName = "OpenAgent";
    public const string DisplayName = "OpenAgent";
    public const string Description = "Multi-channel AI agent platform";
    public const int DefaultHttpPort = 8080;

    /// <summary>
    /// Inspects args[0] for --install / --uninstall / --restart / --status. Returns the exit code
    /// if a command was handled, or null if args do not trigger an install-mode command.
    /// --service is handled separately by the caller (it configures the host, it doesn't exit).
    /// </summary>
    public static int? TryHandle(string[] args)
    {
        if (args.Length == 0)
            return null;

        return args[0] switch
        {
            "--install"   => Install(args),
            "--uninstall" => Uninstall(),
            "--restart"   => Restart(),
            "--status"    => Status(),
            _             => null
        };
    }

    private static int Install(string[] args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("--install is only supported on Windows.");
            return 1;
        }

        var adminCheck = ElevationCheck.RequireAdministrator("--install");
        if (adminCheck != 0)
            return adminCheck;

        var openFirewall = args.Contains("--open-firewall-port");
        var runner = new SystemCommandRunner();

        // Service runs the exe in-place from wherever it was extracted.
        var exePath = Path.Combine(AppContext.BaseDirectory, "OpenAgent.exe");
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"Could not find OpenAgent.exe next to AppContext.BaseDirectory ({AppContext.BaseDirectory}).");
            return 1;
        }

        // Pre-install checks against the current folder.
        foreach (var check in new[]
        {
            PreInstallChecks.VerifyBridgeScriptPresent(AppContext.BaseDirectory),
            PreInstallChecks.VerifyNodeAvailable(runner),
            PreInstallChecks.VerifyPathSafe(exePath)
        })
        {
            if (!check.Ok)
            {
                Console.Error.WriteLine(check.Message);
                return 1;
            }
        }

        var installer = new ServiceInstaller(runner);
        if (installer.IsInstalled(ServiceName))
        {
            Console.Error.WriteLine($"Service {ServiceName} is already registered. Use --uninstall first to re-register, or stop the service and replace files in place to upgrade.");
            return 1;
        }

        installer.Create(ServiceName, exePath, DisplayName, Description);
        EventLogRegistrar.Ensure();

        if (openFirewall)
            new FirewallRule(runner).Add(ServiceName, DefaultHttpPort);

        installer.Start(ServiceName);

        Console.WriteLine($"OpenAgent registered as Windows service '{ServiceName}', running from {AppContext.BaseDirectory}.");
        return 0;
    }

    private static int Uninstall()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("--uninstall is only supported on Windows.");
            return 1;
        }

        var adminCheck = ElevationCheck.RequireAdministrator("--uninstall");
        if (adminCheck != 0)
            return adminCheck;

        var runner = new SystemCommandRunner();
        var installer = new ServiceInstaller(runner);

        if (!installer.IsInstalled(ServiceName))
        {
            Console.Error.WriteLine($"Service {ServiceName} is not installed.");
            return 0;
        }

        installer.Stop(ServiceName);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        installer.Delete(ServiceName);
        new FirewallRule(runner).Remove(ServiceName);

        Console.WriteLine($"{ServiceName} uninstalled. Config, logs, and database preserved.");
        return 0;
    }

    private static int Restart()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("--restart is only supported on Windows.");
            return 1;
        }

        var adminCheck = ElevationCheck.RequireAdministrator("--restart");
        if (adminCheck != 0)
            return adminCheck;

        var installer = new ServiceInstaller(new SystemCommandRunner());
        installer.Stop(ServiceName);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        installer.Start(ServiceName);

        Console.WriteLine($"{ServiceName} restarted.");
        return 0;
    }

    private static int Status()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("--status is only supported on Windows.");
            return 1;
        }

        var runner = new SystemCommandRunner();
        var installer = new ServiceInstaller(runner);
        var installed = installer.IsInstalled(ServiceName);

        Console.WriteLine($"Service installed: {installed}");

        if (installed)
        {
            var result = runner.Run("sc.exe", $"query {ServiceName}");
            Console.WriteLine(result.Output);
        }

        return 0;
    }

}
