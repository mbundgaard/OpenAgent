namespace OpenAgent.Tools.FileSystem;

/// <summary>
/// Enumerates the names of top-level directories under the data root that are reparse points
/// (directory junctions on Windows, symlinks on Linux). Used to produce path-hint error messages
/// and system-prompt guidance. Returns an empty list on deployments without configured symlinks.
/// </summary>
internal static class SymlinkRoots
{
    public static IReadOnlyList<string> List(string dataPath)
    {
        if (!Directory.Exists(dataPath))
            return Array.Empty<string>();

        var names = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(dataPath, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                names.Add(info.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
