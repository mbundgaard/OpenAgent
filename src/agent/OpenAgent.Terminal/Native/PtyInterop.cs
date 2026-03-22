using System.Runtime.InteropServices;

namespace OpenAgent.Terminal.Native;

/// <summary>
/// P/Invoke bindings for Linux PTY operations.
/// Uses posix_openpt + grantpt + unlockpt + ptsname to create the PTY pair,
/// avoiding forkpty (which is unsafe from managed .NET code due to CLR corruption after fork).
/// </summary>
internal static partial class PtyInterop
{
    // --- PTY allocation (safe, no fork) ---

    /// <summary>Opens a new pseudo-terminal master.</summary>
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial int posix_openpt(int flags);

    /// <summary>Grants access to the slave PTY device.</summary>
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial int grantpt(int masterFd);

    /// <summary>Unlocks the slave PTY device for opening.</summary>
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial int unlockpt(int masterFd);

    /// <summary>Returns the path of the slave PTY device (e.g. /dev/pts/3).</summary>
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial nint ptsname(int masterFd);

    // --- File descriptor I/O ---

    /// <summary>Reads bytes from a file descriptor.</summary>
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial nint read(int fd, ref byte buf, nint count);

    /// <summary>Writes bytes to a file descriptor.</summary>
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial nint write(int fd, ref byte buf, nint count);

    /// <summary>Closes a file descriptor.</summary>
    [LibraryImport("libc.so.6", SetLastError = true)]
    internal static partial int close(int fd);

    // --- Terminal control ---

    /// <summary>Sets the terminal window size (TIOCSWINSZ).</summary>
    [LibraryImport("libc.so.6", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int ioctl_winsize(int fd, uint request, ref Winsize winsize);

    // --- Constants ---

    /// <summary>O_RDWR — open for reading and writing.</summary>
    internal const int O_RDWR = 2;

    /// <summary>O_NOCTTY — do not make this the controlling terminal.</summary>
    internal const int O_NOCTTY = 256;

    /// <summary>TIOCSWINSZ ioctl request code for setting window size.</summary>
    internal const uint TIOCSWINSZ = 0x5414;

    // --- Structs ---

    [StructLayout(LayoutKind.Sequential)]
    internal struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }
}
