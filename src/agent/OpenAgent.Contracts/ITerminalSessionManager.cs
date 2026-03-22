namespace OpenAgent.Contracts;

/// <summary>
/// Manages terminal session lifecycle — create, retrieve, close.
/// </summary>
public interface ITerminalSessionManager
{
    /// <summary>Creates a new terminal session with the given working directory.</summary>
    ITerminalSession Create(string sessionId, string workingDirectory);

    /// <summary>Retrieves an existing session by ID, or null if not found.</summary>
    ITerminalSession? Get(string sessionId);

    /// <summary>Closes and disposes a terminal session.</summary>
    Task CloseAsync(string sessionId);
}
