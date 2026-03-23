using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Terminal;

/// <summary>
/// Manages terminal session lifecycle — create, retrieve, close.
/// Uses PTY sessions on Linux, process-based sessions on Windows.
/// Enforces a maximum concurrent session limit and handles cleanup on dispose.
/// </summary>
public sealed class TerminalSessionManager : ITerminalSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ITerminalSession> _sessions = new();
    private readonly ILogger<TerminalSessionManager> _logger;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private const int MaxSessions = 4;

    public TerminalSessionManager(ILogger<TerminalSessionManager> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ITerminalSession Create(string sessionId, string workingDirectory)
    {
        // Atomic check-and-add to prevent race conditions
        _createLock.Wait();
        try
        {
            // Return existing session if another thread created it first
            if (_sessions.TryGetValue(sessionId, out var existing))
                return existing;

            if (_sessions.Count >= MaxSessions)
                throw new InvalidOperationException($"Maximum terminal sessions ({MaxSessions}) reached.");

            var session = CreateSessionForPlatform(workingDirectory);

            _sessions[sessionId] = session;
            _logger.LogInformation("Terminal session created: {SessionId} ({Platform})",
                sessionId, RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "PTY" : "Process");
            return session;
        }
        finally
        {
            _createLock.Release();
        }
    }

    /// <inheritdoc />
    public ITerminalSession? Get(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <inheritdoc />
    public async Task CloseAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;

        await session.DisposeAsync();
        _logger.LogInformation("Terminal session closed: {SessionId}", sessionId);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var (sessionId, session) in _sessions)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing terminal session {SessionId}", sessionId);
            }
        }

        _sessions.Clear();
        _createLock.Dispose();
    }

    /// <summary>
    /// Creates the right terminal session for the current platform.
    /// Linux: PTY-based (full terminal with colors, interactive programs).
    /// Windows: Process-based (redirected streams, basic shell commands).
    /// </summary>
    private ITerminalSession CreateSessionForPlatform(string workingDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new PtyTerminalSession(workingDirectory, cols: 80, rows: 24, _logger);
        }

        return new ProcessTerminalSession(workingDirectory, _logger);
    }
}
