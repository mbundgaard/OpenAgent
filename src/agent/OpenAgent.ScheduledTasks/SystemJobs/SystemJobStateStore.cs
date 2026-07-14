using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenAgent.ScheduledTasks.SystemJobs;

/// <summary>
/// File-backed dictionary of <see cref="SystemJobState"/> keyed by job name. One file holds
/// state for every registered system job — small, easy to inspect, and survives restarts.
/// Not thread-safe by itself; the runner serializes access with a lock.
/// </summary>
public sealed class SystemJobStateStore
{
    /// <summary>Number of times <see cref="Save"/> retries a transient replace failure before giving up.</summary>
    private const int MaxSaveAttempts = 3;

    /// <summary>Delay between retry attempts in <see cref="Save"/>.</summary>
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromMilliseconds(25);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly ILogger<SystemJobStateStore> _logger;
    private Dictionary<string, SystemJobState> _state = new();

    /// <summary>
    /// Creates the store. <paramref name="logger"/> is optional so existing call sites that do
    /// not have DI-supplied logging (tests, direct construction) keep working; it defaults to a
    /// no-op logger.
    /// </summary>
    public SystemJobStateStore(string filePath, ILogger<SystemJobStateStore>? logger = null)
    {
        _filePath = filePath;
        _logger = logger ?? NullLogger<SystemJobStateStore>.Instance;
    }

    /// <summary>
    /// Load state from disk. Missing, corrupt, or momentarily inaccessible files all yield an
    /// empty dictionary rather than throwing — a partially-written file left behind by a hard
    /// kill mid-save must not prevent the host from starting.
    /// </summary>
    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _state = new Dictionary<string, SystemJobState>();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _state = JsonSerializer.Deserialize<Dictionary<string, SystemJobState>>(json) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or momentarily locked state file — start fresh rather than crashing the host.
            _logger.LogWarning(ex,
                "SystemJobStateStore.Load could not read {FilePath}; starting from empty job state",
                _filePath);
            _state = new Dictionary<string, SystemJobState>();
        }
    }

    /// <summary>
    /// Write the current state map to disk atomically. Serializes to a temp file in the same
    /// directory as the target (same volume, so the subsequent move is a rename rather than a
    /// copy) and then replaces the target via <see cref="File.Move(string, string, bool)"/>.
    /// A hard kill mid-write can therefore only ever leave a stray temp file behind - the target
    /// file itself is always either the previous complete content or the new complete content,
    /// never a truncated or partially-written one.
    /// </summary>
    /// <remarks>
    /// Never throws. The replace step can hit a transient sharing violation — most commonly
    /// several independent host instances pointed at the same state file (concurrent test
    /// hosts), or a virus scanner / backup tool briefly holding the target open. That is
    /// retried a few times with a short delay. If it still cannot be replaced, the failure is
    /// logged as a warning and <see cref="Save"/> returns normally: losing one write means a
    /// job's schedule bookkeeping may re-run or re-seed on the next tick, which is recoverable
    /// and must never be allowed to take down host startup.
    /// </remarks>
    public void Save()
    {
        // Everything - including directory creation and serialization - lives inside the try.
        // Save() is called from SystemJobRunner.StartAsync, so any throw here takes down host
        // startup, which is exactly the failure mode this class exists to avoid. A read-only or
        // missing volume, a serialization edge case, or a locked directory must degrade to a
        // dropped save, never an unhandled exception.
        string? tempPath = null;
        var tempFileWritten = false;

        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(_state, JsonOptions);

            tempPath = Path.Combine(directory, $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            tempFileWritten = true;

            for (var attempt = 1; attempt <= MaxSaveAttempts; attempt++)
            {
                try
                {
                    File.Move(tempPath, _filePath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == MaxSaveAttempts)
                    {
                        _logger.LogWarning(ex,
                            "SystemJobStateStore.Save could not replace {FilePath} after {Attempts} attempt(s); this save was dropped",
                            _filePath, MaxSaveAttempts);
                        return;
                    }

                    Thread.Sleep(SaveRetryDelay);
                }
            }
        }
        catch (Exception ex)
        {
            // Broad by design: directory creation, serialization, and the initial temp-file
            // write can all fail for reasons beyond IOException/UnauthorizedAccessException
            // (e.g. PathTooLongException, NotSupportedException on a malformed path). None of
            // them may propagate out of Save().
            _logger.LogWarning(ex,
                "SystemJobStateStore.Save failed for {FilePath}; this save was dropped",
                _filePath);
        }
        finally
        {
            if (tempFileWritten && tempPath is not null)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>Get-or-create the state entry for a job. Never returns null.</summary>
    public SystemJobState GetOrCreate(string name)
    {
        if (!_state.TryGetValue(name, out var state))
        {
            state = new SystemJobState();
            _state[name] = state;
        }
        return state;
    }

    /// <summary>Snapshot of all known job states, keyed by name.</summary>
    public IReadOnlyDictionary<string, SystemJobState> All => _state;
}
