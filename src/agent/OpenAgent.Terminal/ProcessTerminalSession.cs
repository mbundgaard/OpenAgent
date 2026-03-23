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
    private int _inputLength; // tracks typed characters for backspace bounds

    public ProcessTerminalSession(string workingDirectory, ILogger logger)
    {
        _logger = logger;

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? "/Q" : "",
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
            // Redirected stdin has no terminal echo — we echo typed characters manually.
            // cmd.exe echoes the full command after Enter, so we only echo keystrokes,
            // not the Enter itself (to avoid double-display of the command line).
            var raw = data.ToArray();
            using var echoStream = new MemoryStream();
            using var inputStream = new MemoryStream();
            foreach (var b in raw)
            {
                if (b == (byte)'\r')
                {
                    // Echo newline and send \n to shell (cmd /Q suppresses its own echo)
                    echoStream.Write([(byte)'\r', (byte)'\n']);
                    inputStream.WriteByte((byte)'\n');
                    _inputLength = 0;
                }
                else if (b == 0x7f) // DEL — xterm sends this for Backspace
                {
                    if (_inputLength > 0)
                    {
                        echoStream.Write([(byte)'\b', (byte)' ', (byte)'\b']);
                        inputStream.WriteByte(0x08);
                        _inputLength--;
                    }
                }
                else
                {
                    echoStream.WriteByte(b);
                    inputStream.WriteByte(b);
                    _inputLength++;
                }
            }

            if (echoStream.Length > 0)
                _outputChannel.Writer.TryWrite(echoStream.ToArray());

            _process.StandardInput.BaseStream.Write(inputStream.ToArray());
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
