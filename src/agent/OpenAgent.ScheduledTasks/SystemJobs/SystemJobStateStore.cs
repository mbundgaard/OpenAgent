using System.Text;
using System.Text.Json;

namespace OpenAgent.ScheduledTasks.SystemJobs;

/// <summary>
/// File-backed dictionary of <see cref="SystemJobState"/> keyed by job name. One file holds
/// state for every registered system job — small, easy to inspect, and survives restarts.
/// Not thread-safe by itself; the runner serializes access with a lock.
/// </summary>
public sealed class SystemJobStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private Dictionary<string, SystemJobState> _state = new();

    public SystemJobStateStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>Load state from disk. Missing or unreadable files yield an empty dictionary.</summary>
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
        catch (JsonException)
        {
            // Corrupt state file — start fresh rather than crashing the host.
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
    public void Save()
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(_state, JsonOptions);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            throw;
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
