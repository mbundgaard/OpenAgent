namespace OpenAgent.Contracts;

/// <summary>
/// A live PTY terminal session. Write keystrokes in, read output out.
/// </summary>
public interface ITerminalSession : IAsyncDisposable
{
    /// <summary>Writes raw bytes (keystrokes) to the PTY stdin.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Reads PTY output chunks as they arrive.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct);

    /// <summary>Resizes the PTY window.</summary>
    void Resize(int cols, int rows);
}
