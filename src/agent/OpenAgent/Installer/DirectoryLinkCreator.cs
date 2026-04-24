using System.Runtime.InteropServices;

namespace OpenAgent.Installer;

public sealed class DirectoryLinkCreator : IDirectoryLinkCreator
{
    private readonly ISystemCommandRunner _runner;
    private readonly bool _isWindows;

    public DirectoryLinkCreator(ISystemCommandRunner runner)
        : this(runner, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { }

    internal DirectoryLinkCreator(ISystemCommandRunner runner, bool isWindows)
    {
        _runner = runner;
        _isWindows = isWindows;
    }

    public void CreateLink(string linkPath, string targetPath)
    {
        if (_isWindows)
        {
            var result = _runner.Run("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"");
            if (result.ExitCode != 0)
                throw new IOException($"mklink /J failed (exit {result.ExitCode}): {result.Output}");
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
    }

    public bool LinkExists(string linkPath)
    {
        var info = new DirectoryInfo(linkPath);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    public string? ReadLinkTarget(string linkPath)
    {
        var info = new DirectoryInfo(linkPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
            return null;

        return info.LinkTarget
               ?? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
    }
}
