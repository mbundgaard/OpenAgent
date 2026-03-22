using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Terminal;

/// <summary>
/// Terminal session using standard Process with redirected streams.
/// Works on Windows (cmd.exe) and Linux (bash) without PTY/P-Invoke.
/// No true terminal emulation (no colors, no interactive programs like vim),
/// but handles basic shell commands.
/// </summary>
public sealed class ProcessTerminalSession : ITerminalSession
{
    private readonly Process _process;
    private readonly Channel<byte[]> _outputChannel;
    private readonly ILogger _logger;
    private volatile bool _disposed;

    public ProcessTerminalSession(string workingDirectory, ILogger logger)
    {
        _logger = logger;

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["TERM"] = "dumb";
        if (!isWindows)
            psi.Environment["HOME"] = workingDirectory;

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start shell process");

        _outputChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Read stdout and stderr on background threads
        _ = ReadStreamAsync(_process.StandardOutput.BaseStream);
        _ = ReadStreamAsync(_process.StandardError.BaseStream);

        _logger.LogDebug("Process terminal started: pid={Pid}, shell={Shell}",
            _process.Id, psi.FileName);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty) return;

        try
        {
            _process.StandardInput.BaseStream.Write(data);
            _process.StandardInput.BaseStream.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process stdin write failed");
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in _outputChannel.Reader.ReadAllAsync(ct))
        {
            yield return chunk;
        }
    }

    public void Resize(int cols, int rows)
    {
        // No-op for process-based session — no PTY to resize
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

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

        _outputChannel.Writer.TryComplete();
        _process.Dispose();

        _logger.LogDebug("Process terminal session disposed");
    }

    private async Task ReadStreamAsync(System.IO.Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (!_disposed)
            {
                var bytesRead = await stream.ReadAsync(buffer);
                if (bytesRead <= 0) break;

                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                _outputChannel.Writer.TryWrite(chunk);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Process stream read ended");
        }

        _outputChannel.Writer.TryComplete();
    }
}
