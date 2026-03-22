using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;
using OpenAgent.Terminal.Native;

namespace OpenAgent.Terminal;

/// <summary>
/// A terminal session backed by a Linux PTY.
/// Creates a master/slave PTY pair via posix_openpt, then launches bash connected to the slave device.
/// Background thread reads PTY output and pushes into a channel for async consumption.
/// </summary>
public sealed class PtyTerminalSession : ITerminalSession, IAsyncDisposable
{
    // Lock for thread-safe PTY allocation (ptsname returns a static buffer)
    private static readonly object PtyAllocLock = new();

    private readonly int _masterFd;
    private readonly Process _process;
    private readonly Channel<byte[]> _outputChannel;
    private readonly Thread _readThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new PTY session. Allocates the PTY pair, sets initial window size,
    /// launches bash connected to the slave device, and starts the output read loop.
    /// </summary>
    public PtyTerminalSession(string workingDirectory, int cols, int rows, ILogger logger)
    {
        _logger = logger;

        string slavePath;

        // Lock the entire PTY allocation sequence — ptsname is not thread-safe
        lock (PtyAllocLock)
        {
            // Allocate PTY master
            _masterFd = PtyInterop.posix_openpt(PtyInterop.O_RDWR | PtyInterop.O_NOCTTY);
            if (_masterFd < 0)
                throw new InvalidOperationException($"posix_openpt failed: errno {Marshal.GetLastPInvokeError()}");

            // Grant + unlock slave
            if (PtyInterop.grantpt(_masterFd) != 0)
            {
                PtyInterop.close(_masterFd);
                throw new InvalidOperationException($"grantpt failed: errno {Marshal.GetLastPInvokeError()}");
            }
            if (PtyInterop.unlockpt(_masterFd) != 0)
            {
                PtyInterop.close(_masterFd);
                throw new InvalidOperationException($"unlockpt failed: errno {Marshal.GetLastPInvokeError()}");
            }

            // Get slave device path (static buffer — must copy before releasing lock)
            var slavePtr = PtyInterop.ptsname(_masterFd);
            slavePath = Marshal.PtrToStringUTF8(slavePtr)
                ?? throw new InvalidOperationException("ptsname returned null");
        }

        _logger.LogDebug("PTY allocated: master fd={MasterFd}, slave={SlavePath}", _masterFd, slavePath);

        // Set initial window size on master
        var ws = new PtyInterop.Winsize
        {
            ws_row = (ushort)rows,
            ws_col = (ushort)cols
        };
        PtyInterop.ioctl_winsize(_masterFd, PtyInterop.TIOCSWINSZ, ref ws);

        // Launch bash with stdin/stdout/stderr redirected to the slave PTY device.
        // setsid creates a new session so the slave becomes the controlling terminal.
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-c", $"exec setsid bash -i <{slavePath} >{slavePath} 2>&1" },
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["HOME"] = workingDirectory;

        try
        {
            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start bash process");
        }
        catch
        {
            // Clean up PTY master fd if process start fails
            PtyInterop.close(_masterFd);
            throw;
        }

        _logger.LogDebug("Bash started: pid={Pid}, slave={SlavePath}", _process.Id, slavePath);

        // Output channel for async reads
        _outputChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        // Background thread doing blocking reads from master fd
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"PTY-read-{_process.Id}"
        };
        _readThread.Start();
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty) return;

        ref var first = ref MemoryMarshal.GetReference(data);
        var written = PtyInterop.write(_masterFd, ref first, (nint)data.Length);
        if (written < 0)
            _logger.LogWarning("PTY write failed: errno {Errno}", Marshal.GetLastPInvokeError());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in _outputChannel.Reader.ReadAllAsync(ct))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public void Resize(int cols, int rows)
    {
        if (_disposed) return;

        var ws = new PtyInterop.Winsize
        {
            ws_row = (ushort)rows,
            ws_col = (ushort)cols
        };
        PtyInterop.ioctl_winsize(_masterFd, PtyInterop.TIOCSWINSZ, ref ws);
        _logger.LogDebug("PTY resized: {Cols}x{Rows}", cols, rows);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal read thread to stop
        await _cts.CancelAsync();

        // Kill the bash process
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error killing terminal process");
        }

        // Close master fd — this also makes the read thread's read() return -1/EIO
        PtyInterop.close(_masterFd);

        // Wait for read thread to finish
        _readThread.Join(TimeSpan.FromSeconds(2));

        // Complete the channel
        _outputChannel.Writer.TryComplete();
        _process.Dispose();
        _cts.Dispose();

        _logger.LogDebug("PTY session disposed");
    }

    /// <summary>
    /// Blocking read loop on a dedicated thread. Reads from the master fd and pushes
    /// chunks into the output channel until the fd is closed or cancellation is requested.
    /// </summary>
    private void ReadLoop()
    {
        var buffer = new byte[4096];

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                ref var first = ref buffer[0];
                var bytesRead = PtyInterop.read(_masterFd, ref first, (nint)buffer.Length);

                // EOF or error — PTY closed (bash exited or master fd closed)
                if (bytesRead <= 0)
                {
                    _logger.LogDebug("PTY read returned {BytesRead}, ending read loop", bytesRead);
                    break;
                }

                // Copy the read bytes and push to channel
                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, (int)bytesRead);

                if (!_outputChannel.Writer.TryWrite(chunk))
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "PTY read loop ended with exception");
        }

        _outputChannel.Writer.TryComplete();
    }
}
