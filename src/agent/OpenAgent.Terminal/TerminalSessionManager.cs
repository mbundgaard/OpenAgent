using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts;

namespace OpenAgent.Terminal;

/// <summary>
/// Manages terminal session lifecycle — create, retrieve, close.
/// Enforces a maximum concurrent session limit and handles cleanup on dispose.
/// </summary>
public sealed class TerminalSessionManager : ITerminalSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ITerminalSession> _sessions = new();
    private readonly ILogger<TerminalSessionManager> _logger;
    private const int MaxSessions = 4;

    public TerminalSessionManager(ILogger<TerminalSessionManager> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ITerminalSession Create(string sessionId, string workingDirectory)
    {
        if (_sessions.Count >= MaxSessions)
            throw new InvalidOperationException($"Maximum terminal sessions ({MaxSessions}) reached.");

        var session = new PtyTerminalSession(
            workingDirectory,
            cols: 80,
            rows: 24,
            _logger);

        if (!_sessions.TryAdd(sessionId, session))
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException($"Terminal session '{sessionId}' already exists.");
        }

        _logger.LogInformation("Terminal session created: {SessionId}", sessionId);
        return session;
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
    }
}
