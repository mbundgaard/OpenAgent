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
    public const string DefaultInstallPath = @"C:\OpenAgent";
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

        var installPath = ParseOptionalArg(args, "--path") ?? DefaultInstallPath;
        var openFirewall = args.Contains("--open-firewall-port");

        var runner = new SystemCommandRunner();

        // Pre-install checks
        var sourceFolder = AppContext.BaseDirectory;
        foreach (var check in new[]
        {
            PreInstallChecks.VerifyBridgeScriptPresent(sourceFolder),
            PreInstallChecks.VerifyNodeAvailable(runner),
            PreInstallChecks.VerifyPathSafe(installPath)
        })
        {
            if (!check.Ok)
            {
                Console.Error.WriteLine(check.Message);
                return 1;
            }
        }

        var installer = new ServiceInstaller(runner);
        var reinstall = installer.IsInstalled(ServiceName);

        if (reinstall)
        {
            Console.WriteLine($"Existing service found; stopping for upgrade.");
            installer.Stop(ServiceName);
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        // Copy source folder contents to install path (binary + node/, skip any data folders that might be present)
        Directory.CreateDirectory(installPath);
        CopyInstallArtifacts(sourceFolder, installPath);

        var exePath = Path.Combine(installPath, "OpenAgent.exe");

        if (reinstall)
        {
            installer.UpdateBinPath(ServiceName, exePath);
        }
        else
        {
            installer.Create(ServiceName, exePath, DisplayName, Description);
        }

        EventLogRegistrar.Ensure();

        if (openFirewall)
            new FirewallRule(runner).Add(ServiceName, DefaultHttpPort);

        installer.Start(ServiceName);

        Console.WriteLine($"OpenAgent {(reinstall ? "upgraded" : "installed")} at {installPath} and started.");
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

    private static string? ParseOptionalArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    private static void CopyInstallArtifacts(string sourceFolder, string installPath)
    {
        // Copy the exe itself
        var sourceExe = Path.Combine(sourceFolder, "OpenAgent.exe");
        if (File.Exists(sourceExe))
            File.Copy(sourceExe, Path.Combine(installPath, "OpenAgent.exe"), overwrite: true);

        // Copy any .dll / .pdb / .json adjacent to the exe (appsettings, etc.)
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("OpenAgent.exe", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(installPath, name), overwrite: true);
        }

        // Copy bundled directories (node bridge, React UI). Replace contents fully on upgrade.
        foreach (var dirName in new[] { "node", "wwwroot" })
        {
            var sourceDir = Path.Combine(sourceFolder, dirName);
            if (!Directory.Exists(sourceDir)) continue;

            var destDir = Path.Combine(installPath, dirName);
            if (Directory.Exists(destDir))
                Directory.Delete(destDir, recursive: true);
            CopyDirectory(sourceDir, destDir);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
