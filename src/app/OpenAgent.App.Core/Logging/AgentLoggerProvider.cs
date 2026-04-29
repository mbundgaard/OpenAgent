using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenAgent.App.Core.Api;

namespace OpenAgent.App.Core.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> that buffers log entries in memory and periodically
/// flushes them to the agent's <c>POST /api/client-logs</c> endpoint. The provider is
/// safe to construct before DI is fully built — it resolves <see cref="IApiClient"/>
/// lazily through the supplied accessor on every flush.
/// </summary>
/// <remarks>
/// The buffer is bounded; once full, oldest entries are dropped to make room. The HTTP
/// flush runs on a background timer (default 5s). Failures during flush are silent —
/// we never log from inside the flush loop because that could create a feedback storm.
/// </remarks>
public sealed class AgentLoggerProvider : ILoggerProvider
{
    private const int MaxBufferedLines = 500;
    private const int MaxLinesPerFlush = 100;
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<ClientLogLine> _buffer = new();
    private readonly Func<IApiClient?> _apiClientAccessor;
    private readonly Timer _timer;
    private readonly LogLevel _minimumLevel;
    private int _flushing;
    private bool _disposed;

    /// <summary>
    /// Create the provider. <paramref name="apiClientAccessor"/> is invoked on each flush
    /// to resolve the current <see cref="IApiClient"/>; it may return null if credentials
    /// haven't been set yet (in which case lines stay buffered).
    /// </summary>
    public AgentLoggerProvider(Func<IApiClient?> apiClientAccessor, LogLevel minimumLevel = LogLevel.Debug)
    {
        _apiClientAccessor = apiClientAccessor;
        _minimumLevel = minimumLevel;
        _timer = new Timer(_ => _ = FlushAsync(), null, DefaultFlushInterval, DefaultFlushInterval);
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new AgentLogger(categoryName, this);

    internal LogLevel MinimumLevel => _minimumLevel;

    internal void Enqueue(ClientLogLine line)
    {
        if (_disposed) return;
        // Bound the buffer — drop oldest if full.
        while (_buffer.Count >= MaxBufferedLines && _buffer.TryDequeue(out _)) { }
        _buffer.Enqueue(line);
    }

    /// <summary>Flush queued entries to the agent. Safe to call concurrently — only one flush runs at a time.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            var client = _apiClientAccessor();
            if (client is null) return;

            var batch = new List<ClientLogLine>(MaxLinesPerFlush);
            while (batch.Count < MaxLinesPerFlush && _buffer.TryDequeue(out var line))
                batch.Add(line);
            if (batch.Count == 0) return;

            try
            {
                await client.PostClientLogsAsync(batch, ct);
            }
            catch
            {
                // Swallow: never log from inside the flush, would feedback-loop.
                // Lines are dropped — this is a diagnostic logger, not an audit log.
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        // Best-effort final flush
        try { FlushAsync().GetAwaiter().GetResult(); } catch { }
    }
}

/// <summary>The <see cref="ILogger"/> instance handed back by <see cref="AgentLoggerProvider"/>.</summary>
internal sealed class AgentLogger : ILogger
{
    private readonly string _category;
    private readonly AgentLoggerProvider _provider;

    public AgentLogger(string category, AgentLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.MinimumLevel && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null)
            message += $" | EXC {exception.GetType().Name}: {exception.Message}";
        _provider.Enqueue(new ClientLogLine
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = logLevel.ToString(),
            Category = _category,
            Message = message,
        });
    }
}
