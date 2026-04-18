namespace OpenAgent.Installer;

/// <summary>
/// Creates directory links that behave like transparent path mounts:
/// directory junctions on Windows (no admin needed), symlinks on Linux.
/// </summary>
public interface IDirectoryLinkCreator
{
    /// <summary>Creates a directory link at <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>.</summary>
    void CreateLink(string linkPath, string targetPath);

    /// <summary>Returns true if a link (junction or symlink) exists at the path.</summary>
    bool LinkExists(string linkPath);

    /// <summary>Returns the resolved target of an existing link, or null if the path is not a link.</summary>
    string? ReadLinkTarget(string linkPath);
}
